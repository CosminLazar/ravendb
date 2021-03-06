﻿using System.Diagnostics;
using System.Text;

using Raven.Abstractions.Util.Encryptors;
using Raven.Abstractions.Util.Streams;
using Raven.Client.Extensions;
using Raven.Database.Storage.Voron.StorageActions.StructureSchemas;
using Raven.Database.Util;

namespace Raven.Database.Storage.Voron.StorageActions
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;

	using Raven.Abstractions;
	using Raven.Abstractions.Data;
	using Raven.Abstractions.Extensions;
	using Raven.Abstractions.MEF;
	using Raven.Database.Impl;
	using Raven.Database.Indexing;
	using Raven.Database.Plugins;
	using Raven.Database.Storage.Voron.Impl;
	using Raven.Json.Linq;

	using global::Voron;
	using global::Voron.Impl;

	using Index = Raven.Database.Storage.Voron.Impl.Index;

	internal class MappedResultsStorageActions : StorageActionsBase, IMappedResultsStorageAction
	{
		private readonly TableStorage tableStorage;

		private readonly IUuidGenerator generator;

		private readonly Reference<WriteBatch> writeBatch;
		private readonly IStorageActionsAccessor storageActionsAccessor;

		private readonly OrderedPartCollection<AbstractDocumentCodec> documentCodecs;

        public MappedResultsStorageActions(TableStorage tableStorage, IUuidGenerator generator, OrderedPartCollection<AbstractDocumentCodec> documentCodecs, Reference<SnapshotReader> snapshot, Reference<WriteBatch> writeBatch, IBufferPool bufferPool, IStorageActionsAccessor storageActionsAccessor)
			: base(snapshot, bufferPool)
		{
			this.tableStorage = tableStorage;
			this.generator = generator;
			this.documentCodecs = documentCodecs;
			this.writeBatch = writeBatch;
	        this.storageActionsAccessor = storageActionsAccessor;
		}

		public IEnumerable<ReduceKeyAndCount> GetKeysStats(int view, int start, int pageSize)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);
			using (var iterator = reduceKeyCountsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ReduceKeyCounts, iterator.CurrentKey, writeBatch.Value, out version);

					Debug.Assert(value != null);
					yield return new ReduceKeyAndCount
					{
						Count = value.ReadInt(ReduceKeyCountFields.MappedItemsCount),
						Key = value.ReadString(ReduceKeyCountFields.ReduceKey)
					};

					count++;
				}
				while (iterator.MoveNext() && count < pageSize);
			}
		}

		public void PutMappedResult(int view, string docId, string reduceKey, RavenJObject data)
		{
			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);

			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

            var ms = CreateStream();
			using (var stream = documentCodecs.Aggregate((Stream) new UndisposableStream(ms), (ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
			{
				data.WriteTo(stream);
				stream.Flush();
			}

			var id = generator.CreateSequentialUuid(UuidType.MappedResults);
			var idAsString = id.ToString();
			var bucket = IndexingUtil.MapBucket(docId);

		    var reduceKeyHash = HashKey(reduceKey);

			var mappedResult = new Structure<MappedResultFields>(tableStorage.MappedResults.Schema)
				.Set(MappedResultFields.IndexId, view)
				.Set(MappedResultFields.Bucket, bucket)
				.Set(MappedResultFields.Timestamp, SystemTime.UtcNow.ToBinary())
				.Set(MappedResultFields.ReduceKey, reduceKey)
				.Set(MappedResultFields.DocId, docId)
				.Set(MappedResultFields.Etag, id.ToByteArray());

			tableStorage.MappedResults.AddStruct(
				writeBatch.Value,
				idAsString,
				mappedResult, 0);

			ms.Position = 0;
			mappedResultsData.Add(writeBatch.Value, idAsString, ms, 0);

			mappedResultsByViewAndDocumentId.MultiAdd(writeBatch.Value, CreateKey(view, docId), idAsString);
			mappedResultsByView.MultiAdd(writeBatch.Value, CreateKey(view), idAsString);
            mappedResultsByViewAndReduceKey.MultiAdd(writeBatch.Value, CreateKey(view, reduceKey, reduceKeyHash), idAsString);
            mappedResultsByViewAndReduceKeyAndSourceBucket.MultiAdd(writeBatch.Value, CreateKey(view, reduceKey, reduceKeyHash, bucket), idAsString);
		}

		public void IncrementReduceKeyCounter(int view, string reduceKey, int val)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var key = CreateKey(view, reduceKey, reduceKeyHash);

			ushort version;
			var value = LoadStruct(tableStorage.ReduceKeyCounts, key, writeBatch.Value, out version);

			var newValue = val;
			if (value != null)
				newValue += value.ReadInt(ReduceKeyCountFields.MappedItemsCount);

			AddReduceKeyCount(key, view, reduceKey, newValue, version);
		}

		private void DecrementReduceKeyCounter(int view, string reduceKey, int val)
		{
            var reduceKeyHash = HashKey(reduceKey);
			var key = CreateKey(view, reduceKey, reduceKeyHash);

			ushort reduceKeyCountVersion;
			var reduceKeyCount = LoadStruct(tableStorage.ReduceKeyCounts, key, writeBatch.Value, out reduceKeyCountVersion);

			var newValue = -val;
			if (reduceKeyCount != null)
			{
				var currentValue = reduceKeyCount.ReadInt(ReduceKeyCountFields.MappedItemsCount);
				if (currentValue == val)
				{
					var reduceKeyTypeVersion = tableStorage.ReduceKeyTypes.ReadVersion(Snapshot, key, writeBatch.Value);

					DeleteReduceKeyCount(key, view, reduceKeyCountVersion);
					DeleteReduceKeyType(key, view, reduceKeyTypeVersion);
					return;
				}

				newValue += currentValue;
			}

			AddReduceKeyCount(key, view, reduceKey, newValue, reduceKeyCountVersion);
		}

		public void DeleteMappedResultsForDocumentId(string documentId, int view, Dictionary<ReduceKeyAndBucket, int> removed)
		{
			var viewAndDocumentId = CreateKey(view, documentId);

			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			using (var iterator = mappedResultsByViewAndDocumentId.MultiRead(Snapshot, viewAndDocumentId))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					ushort version;
					var value = LoadStruct(tableStorage.MappedResults, id, writeBatch.Value, out version);
					var reduceKey = value.ReadString(MappedResultFields.ReduceKey);
					var bucket = value.ReadInt(MappedResultFields.Bucket);

					DeleteMappedResult(id, view, documentId, reduceKey, bucket.ToString(CultureInfo.InvariantCulture));

					var reduceKeyAndBucket = new ReduceKeyAndBucket(bucket, reduceKey);
					removed[reduceKeyAndBucket] = removed.GetOrDefault(reduceKeyAndBucket) + 1;
				}
				while (iterator.MoveNext());
			}
		}

		public void UpdateRemovedMapReduceStats(int view, Dictionary<ReduceKeyAndBucket, int> removed)
		{
			foreach (var keyAndBucket in removed)
			{
				DecrementReduceKeyCounter(view, keyAndBucket.Key.ReduceKey, keyAndBucket.Value);
			}
		}

		public void DeleteMappedResultsForView(int view)
		{
			var deletedReduceKeys = new List<string>();
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);

			using (var iterator = mappedResultsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					ushort version;
					var value = LoadStruct(tableStorage.MappedResults, id, writeBatch.Value, out version);
					var reduceKey = value.ReadString(MappedResultFields.ReduceKey);
					var bucket = value.ReadInt(MappedResultFields.Bucket);
					var docId = value.ReadString(MappedResultFields.DocId);

					DeleteMappedResult(id, view, docId, reduceKey, bucket.ToString(CultureInfo.InvariantCulture));

					deletedReduceKeys.Add(reduceKey);
					storageActionsAccessor.General.MaybePulseTransaction();
				}
				while (iterator.MoveNext());
			}

			foreach (var g in deletedReduceKeys.GroupBy(x => x, StringComparer.InvariantCultureIgnoreCase))
			{
				DecrementReduceKeyCounter(view, g.Key, g.Count());
			}
		}

		public IEnumerable<string> GetKeysForIndexForDebug(int view, string startsWith, string sourceId, int start, int take)
		{
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			using (var iterator = mappedResultsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys)) 
					return Enumerable.Empty<string>();

				var results = new List<string>();
				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);

					if (string.IsNullOrEmpty(sourceId) == false)
					{
						var docId = value.ReadString(MappedResultFields.DocId);
						if (string.Equals(sourceId, docId, StringComparison.OrdinalIgnoreCase) == false)
							continue;
					}

					var reduceKey = value.ReadString(MappedResultFields.ReduceKey);

					if (string.IsNullOrEmpty(startsWith) == false && reduceKey.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase) == false)
						continue;

					results.Add(reduceKey);
				}
				while (iterator.MoveNext());

				return results
					.Distinct()
					.Skip(start)
					.Take(take);
			}
		}

        public IEnumerable<MappedResultInfo> GetMappedResultsForDebug(int view, string reduceKey, int start, int take)
        {
            var reduceKeyHash = HashKey(reduceKey);
            var viewAndReduceKey = CreateKey(view, reduceKey, reduceKeyHash);
            var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
            var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

            using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
            {
                if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
                    yield break;

                var count = 0;
                do
                {
                    ushort version;
                    var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);
                    var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);
                    yield return new MappedResultInfo
                    {
                        ReduceKey = value.ReadString(MappedResultFields.ReduceKey),
                        Etag = Etag.Parse(value.ReadBytes(MappedResultFields.Etag)),
                        Timestamp = DateTime.FromBinary(value.ReadLong(MappedResultFields.Timestamp)),
                        Bucket = value.ReadInt(MappedResultFields.Bucket),
                        Source = value.ReadString(MappedResultFields.DocId),
                        Size = size,
						Data = LoadMappedResult(iterator.CurrentKey, value.ReadString(MappedResultFields.ReduceKey), mappedResultsData)
                    };

                    count++;
                }
                while (iterator.MoveNext() && count < take);
            }
        }

        public IEnumerable<string> GetSourcesForIndexForDebug(int view, string startsWith, int take)
        {
            var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
            using (var iterator = mappedResultsByView.MultiRead(Snapshot, CreateKey(view)))
            {
                if (!iterator.Seek(Slice.BeforeAllKeys))
                    return Enumerable.Empty<string>();

                var results = new HashSet<string>();
                do
                {
                    ushort version;
                    var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);

                    var docId = value.ReadString(MappedResultFields.DocId);

                    if (string.IsNullOrEmpty(startsWith) == false && docId.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase) == false)
                        continue;

                    results.Add(docId);
                }
                while (iterator.MoveNext() && results.Count <= take);

                return results;
            }
        }
       

		public IEnumerable<MappedResultInfo> GetReducedResultsForDebug(int view, string reduceKey, int level, int start, int take)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var viewAndReduceKeyAndLevel = CreateKey(view, reduceKey, reduceKeyHash, level);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

			using (var iterator = reduceResultsByViewAndReduceKeyAndLevel.MultiRead(Snapshot, viewAndReduceKeyAndLevel))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ReduceResults, iterator.CurrentKey, writeBatch.Value, out version);
					var size = tableStorage.ReduceResults.GetDataSize(Snapshot, iterator.CurrentKey);

					var readReduceKey = value.ReadString(ReduceResultFields.ReduceKey);

					yield return
						new MappedResultInfo
						{
							ReduceKey = readReduceKey,
							Etag = Etag.Parse(value.ReadBytes(ReduceResultFields.Etag)),
							Timestamp = DateTime.FromBinary(value.ReadLong(ReduceResultFields.Timestamp)),
							Bucket = value.ReadInt(ReduceResultFields.Bucket),
							Source = value.ReadInt(ReduceResultFields.SourceBucket).ToString(),
							Size = size,
							Data = LoadMappedResult(iterator.CurrentKey, readReduceKey, reduceResultsData)
						};

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public IEnumerable<ScheduledReductionDebugInfo> GetScheduledReductionForDebug(int view, int start, int take)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			using (var iterator = scheduledReductionsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ScheduledReductions, iterator.CurrentKey, writeBatch.Value, out version);

					yield return new ScheduledReductionDebugInfo
					{
						Key = value.ReadString(ScheduledReductionFields.ReduceKey),
						Bucket = value.ReadInt(ScheduledReductionFields.Bucket),
						Etag = new Guid(value.ReadBytes(ScheduledReductionFields.Etag)),
						Level = value.ReadInt(ScheduledReductionFields.Level),
						Timestamp = DateTime.FromBinary(value.ReadLong(ScheduledReductionFields.Timestamp)),
					};

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public void ScheduleReductions(int view, int level, ReduceKeyAndBucket reduceKeysAndBuckets)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);

			var id = generator.CreateSequentialUuid(UuidType.ScheduledReductions);
			var idAsString = id.ToString();
		    var reduceHashKey = HashKey(reduceKeysAndBuckets.ReduceKey);

			var scheduledReduction = new Structure<ScheduledReductionFields>(tableStorage.ScheduledReductions.Schema);

			scheduledReduction.Set(ScheduledReductionFields.IndexId, view)
				.Set(ScheduledReductionFields.ReduceKey, reduceKeysAndBuckets.ReduceKey)
				.Set(ScheduledReductionFields.Bucket, reduceKeysAndBuckets.Bucket)
				.Set(ScheduledReductionFields.Level, level)
				.Set(ScheduledReductionFields.Etag, id.ToByteArray())
				.Set(ScheduledReductionFields.Timestamp, SystemTime.UtcNow.ToBinary());

			tableStorage.ScheduledReductions.AddStruct(writeBatch.Value, idAsString, scheduledReduction);

			scheduledReductionsByView.MultiAdd(writeBatch.Value, CreateKey(view), idAsString);
			scheduledReductionsByViewAndLevelAndReduceKey.MultiAdd(writeBatch.Value, CreateKey(view, level, reduceKeysAndBuckets.ReduceKey, reduceHashKey), idAsString);
		}

		public IEnumerable<MappedResultInfo> GetItemsToReduce(GetItemsToReduceParams getItemsToReduceParams)
		{
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);
            var deleter = new ScheduledReductionDeleter(getItemsToReduceParams.ItemsToDelete, o =>
            {
                var etag = o as Etag;
                if (etag == null) 
                    return null;

                return etag.ToString();
            });

			var seenLocally = new HashSet<Tuple<string, int>>();
			foreach (var reduceKey in getItemsToReduceParams.ReduceKeys.ToArray())
			{
			    var reduceKeyHash = HashKey(reduceKey);
                var viewAndLevelAndReduceKey = CreateKey(getItemsToReduceParams.Index, getItemsToReduceParams.Level, reduceKey, reduceKeyHash);
				using (var iterator = scheduledReductionsByViewAndLevelAndReduceKey.MultiRead(Snapshot, viewAndLevelAndReduceKey))
				{
					if (!iterator.Seek(Slice.BeforeAllKeys))
						continue;

					do
					{
						if (getItemsToReduceParams.Take <= 0)
							break;

						ushort version;
						var value = LoadStruct(tableStorage.ScheduledReductions, iterator.CurrentKey, writeBatch.Value, out version);

						var reduceKeyFromDb = value.ReadString(ScheduledReductionFields.ReduceKey);

						var bucket = value.ReadInt(ScheduledReductionFields.Bucket);
						var rowKey = Tuple.Create(reduceKeyFromDb, bucket);
					    var thisIsNewScheduledReductionRow = deleter.Delete(iterator.CurrentKey, Etag.Parse(value.ReadBytes(ScheduledReductionFields.Etag)));
						var neverSeenThisKeyAndBucket = getItemsToReduceParams.ItemsAlreadySeen.Add(rowKey);
						if (thisIsNewScheduledReductionRow || neverSeenThisKeyAndBucket)
						{
							if (seenLocally.Add(rowKey))
							{
								foreach (var mappedResultInfo in GetResultsForBucket(getItemsToReduceParams.Index, getItemsToReduceParams.Level, reduceKeyFromDb, bucket, getItemsToReduceParams.LoadData))
								{
									getItemsToReduceParams.Take--;
									yield return mappedResultInfo;
								}
							}
						}

						if (getItemsToReduceParams.Take <= 0)
							yield break;
					}
					while (iterator.MoveNext());
				}

				getItemsToReduceParams.ReduceKeys.Remove(reduceKey);

				if (getItemsToReduceParams.Take <= 0)
                    yield break;
			}
		}

		private IEnumerable<MappedResultInfo> GetResultsForBucket(int view, int level, string reduceKey, int bucket, bool loadData)
		{
			switch (level)
			{
				case 0:
					return GetMappedResultsForBucket(view, reduceKey, bucket, loadData);
				case 1:
				case 2:
					return GetReducedResultsForBucket(view, reduceKey, level, bucket, loadData);
				default:
					throw new ArgumentException("Invalid level: " + level);
			}
		}

		private IEnumerable<MappedResultInfo> GetReducedResultsForBucket(int view, string reduceKey, int level, int bucket, bool loadData)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var viewAndReduceKeyAndLevelAndBucket = CreateKey(view, reduceKey, reduceKeyHash, level, bucket);

			var reduceResultsByViewAndReduceKeyAndLevelAndBucket = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndBucket);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);
			using (var iterator = reduceResultsByViewAndReduceKeyAndLevelAndBucket.MultiRead(Snapshot, viewAndReduceKeyAndLevelAndBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
				{
					yield return new MappedResultInfo
								 {
									 Bucket = bucket,
									 ReduceKey = reduceKey
								 };

					yield break;
				}

				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ReduceResults, iterator.CurrentKey, writeBatch.Value, out version);
					var size = tableStorage.ReduceResults.GetDataSize(Snapshot, iterator.CurrentKey);

					var readReduceKey = value.ReadString(ReduceResultFields.ReduceKey);

					yield return new MappedResultInfo
					{
						ReduceKey = readReduceKey,
						Etag = Etag.Parse(value.ReadBytes(ReduceResultFields.Etag)),
						Timestamp = DateTime.FromBinary(value.ReadLong(ReduceResultFields.Timestamp)),
						Bucket = value.ReadInt(ReduceResultFields.Bucket),
						Source = null,
						Size = size,
						Data = loadData ? LoadMappedResult(iterator.CurrentKey, readReduceKey, reduceResultsData) : null
					};
				}
				while (iterator.MoveNext());
			}
		}

		private IEnumerable<MappedResultInfo> GetMappedResultsForBucket(int view, string reduceKey, int bucket, bool loadData)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var viewAndReduceKeyAndSourceBucket = CreateKey(view, reduceKey, reduceKeyHash, bucket);

			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			using (var iterator = mappedResultsByViewAndReduceKeyAndSourceBucket.MultiRead(Snapshot, viewAndReduceKeyAndSourceBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
				{
					yield return new MappedResultInfo
					{
						Bucket = bucket,
						ReduceKey = reduceKey
					};

					yield break;
				}

				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);
					var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);

					var readReduceKey = value.ReadString(MappedResultFields.ReduceKey);
					yield return new MappedResultInfo
					{
						ReduceKey = readReduceKey,
						Etag = Etag.Parse(value.ReadBytes(MappedResultFields.Etag)),
						Timestamp = DateTime.FromBinary(value.ReadLong(MappedResultFields.Timestamp)),
						Bucket = value.ReadInt(MappedResultFields.Bucket),
						Source = null,
						Size = size,
						Data = loadData ? LoadMappedResult(iterator.CurrentKey, readReduceKey, mappedResultsData) : null
					};
				}
				while (iterator.MoveNext());
			}
		}

		public ScheduledReductionInfo DeleteScheduledReduction(IEnumerable<object> itemsToDelete)
		{
			if (itemsToDelete == null)
				return null;

			var result = new ScheduledReductionInfo();
			var hasResult = false;
			var currentEtag = Etag.Empty;
			foreach (Etag etag in itemsToDelete)
			{
				var etagAsString = etag.ToString();

				ushort version;
				var value = LoadStruct(tableStorage.ScheduledReductions, etagAsString, writeBatch.Value, out version);
				if (value == null)
					continue;

				if (etag.CompareTo(currentEtag) > 0)
				{
					hasResult = true;
					result.Etag = etag;
					result.Timestamp = DateTime.FromBinary(value.ReadLong(ScheduledReductionFields.Timestamp));
				}

				var view = value.ReadInt(ScheduledReductionFields.IndexId);
				var level = value.ReadInt(ScheduledReductionFields.Level);
				var reduceKey = value.ReadString(ScheduledReductionFields.ReduceKey);

				DeleteScheduledReduction(etagAsString, view, level, reduceKey);
			}

			return hasResult ? result : null;
		}

		public void DeleteScheduledReduction(int view, int level, string reduceKey)
		{
		    var reduceKeyHash = HashKey(reduceKey);
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);
            using (var iterator = scheduledReductionsByViewAndLevelAndReduceKey.MultiRead(Snapshot, CreateKey(view, level, reduceKey, reduceKeyHash)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey;
					DeleteScheduledReduction(id, view, level, reduceKey);
				}
				while (iterator.MoveNext());
			}
		}

		public void PutReducedResult(int view, string reduceKey, int level, int sourceBucket, int bucket, RavenJObject data)
		{
			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevelAndBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndBucket);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsByView =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByView);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

            var ms = CreateStream();
			using (
				var stream = documentCodecs.Aggregate((Stream) new UndisposableStream(ms),
					(ds, codec) => codec.Value.Encode(reduceKey, data, null, ds)))
			{
				data.WriteTo(stream);
				stream.Flush();
			}

			var id = generator.CreateSequentialUuid(UuidType.MappedResults);
			var idAsString = id.ToString();
		    var reduceKeyHash = HashKey(reduceKey);

			var reduceResult = new Structure<ReduceResultFields>(tableStorage.ReduceResults.Schema)
				.Set(ReduceResultFields.IndexId, view)
				.Set(ReduceResultFields.Etag, id.ToByteArray())
				.Set(ReduceResultFields.ReduceKey, reduceKey)
				.Set(ReduceResultFields.Level, level)
				.Set(ReduceResultFields.SourceBucket, sourceBucket)
				.Set(ReduceResultFields.Bucket, bucket)
				.Set(ReduceResultFields.Timestamp, SystemTime.UtcNow.ToBinary());

			tableStorage.ReduceResults.AddStruct(writeBatch.Value, idAsString, reduceResult, 0);

			ms.Position = 0;
			reduceResultsData.Add(writeBatch.Value, idAsString, ms, 0);

            var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, reduceKeyHash, level, sourceBucket);
            var viewAndReduceKeyAndLevel = CreateKey(view, reduceKey, reduceKeyHash, level);
            var viewAndReduceKeyAndLevelAndBucket = CreateKey(view, reduceKey, reduceKeyHash, level, bucket);

			reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiAdd(writeBatch.Value, viewAndReduceKeyAndLevelAndSourceBucket, idAsString);
			reduceResultsByViewAndReduceKeyAndLevel.MultiAdd(writeBatch.Value, viewAndReduceKeyAndLevel, idAsString);
			reduceResultsByViewAndReduceKeyAndLevelAndBucket.MultiAdd(writeBatch.Value, viewAndReduceKeyAndLevelAndBucket, idAsString);
			reduceResultsByView.MultiAdd(writeBatch.Value, CreateKey(view), idAsString);
		}

		public void RemoveReduceResults(int view, int level, string reduceKey, int sourceBucket)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, reduceKeyHash, level, sourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);

			using (var iterator = reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiRead(Snapshot, viewAndReduceKeyAndLevelAndSourceBucket))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					RemoveReduceResult(iterator.CurrentKey.Clone());
				}
				while (iterator.MoveNext());
			}
		}

		public IEnumerable<ReduceTypePerKey> GetReduceTypesPerKeys(int view, int take, int limitOfItemsToReduceInSingleStep)
		{
			if (take <= 0)
				take = 1;

			var allKeysToReduce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var key = CreateKey(view);
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
            using (var iterator = scheduledReductionsByView.MultiRead(Snapshot, key))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					yield break;

                var processedItems = 0;

				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ScheduledReductions, iterator.CurrentKey, writeBatch.Value, out version);

					allKeysToReduce.Add(value.ReadString(ScheduledReductionFields.ReduceKey));
                    processedItems++;
				}
				while (iterator.MoveNext() && processedItems < take);
			}

            foreach (var reduceKey in allKeysToReduce)
            {
                var count = GetNumberOfMappedItemsPerReduceKey(view, reduceKey);
                var reduceType = count >= limitOfItemsToReduceInSingleStep ? ReduceType.MultiStep : ReduceType.SingleStep;
                yield return new ReduceTypePerKey(reduceKey, reduceType);
            }
		}

		private int GetNumberOfMappedItemsPerReduceKey(int view, string reduceKey)
		{
		    var reduceKeyHash = HashKey(reduceKey);
            var key = CreateKey(view, reduceKey, reduceKeyHash);

			ushort version;
			var value = LoadStruct(tableStorage.ReduceKeyCounts, key, writeBatch.Value, out version);
			if (value == null)
				return 0;

			return value.ReadInt(ReduceKeyCountFields.MappedItemsCount);
		}

		public void UpdatePerformedReduceType(int view, string reduceKey, ReduceType reduceType)
		{
            var reduceKeyHash = HashKey(reduceKey);
			var key = CreateKey(view, reduceKey, reduceKeyHash);
			var version = tableStorage.ReduceKeyTypes.ReadVersion(Snapshot, key, writeBatch.Value);

			AddReduceKeyType(key, view, reduceKey, reduceType, version);
		}

		private void DeleteReduceKeyCount(string key, int view, ushort? expectedVersion)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);

			tableStorage.ReduceKeyCounts.Delete(writeBatch.Value, key, expectedVersion);
			reduceKeyCountsByView.MultiDelete(writeBatch.Value, CreateKey(view), key);
		}

		private void DeleteReduceKeyType(string key, int view, ushort? expectedVersion)
		{
			var reduceKeyTypesByView = tableStorage.ReduceKeyTypes.GetIndex(Tables.ReduceKeyTypes.Indices.ByView);

			tableStorage.ReduceKeyTypes.Delete(writeBatch.Value, key, expectedVersion);
			reduceKeyTypesByView.MultiDelete(writeBatch.Value, CreateKey(view), key);
		}

		private void AddReduceKeyCount(string key, int view, string reduceKey, int count, ushort? expectedVersion)
		{
			var reduceKeyCountsByView = tableStorage.ReduceKeyCounts.GetIndex(Tables.ReduceKeyCounts.Indices.ByView);

			tableStorage.ReduceKeyCounts.AddStruct(
				writeBatch.Value,
				key,
				new Structure<ReduceKeyCountFields>(tableStorage.ReduceKeyCounts.Schema)
					.Set(ReduceKeyCountFields.IndexId, view)
					.Set(ReduceKeyCountFields.MappedItemsCount, count)
					.Set(ReduceKeyCountFields.ReduceKey, reduceKey), 
				expectedVersion);

			reduceKeyCountsByView.MultiAdd(writeBatch.Value, CreateKey(view), key);
		}

		private void AddReduceKeyType(string key, int view, string reduceKey, ReduceType status, ushort? expectedVersion)
		{
			var reduceKeyTypesByView = tableStorage.ReduceKeyTypes.GetIndex(Tables.ReduceKeyTypes.Indices.ByView);

			tableStorage.ReduceKeyTypes.AddStruct(
				writeBatch.Value,
				key,
				new Structure<ReduceKeyTypeFields>(tableStorage.ReduceKeyTypes.Schema)
					.Set(ReduceKeyTypeFields.IndexId, view)
					.Set(ReduceKeyTypeFields.ReduceType, (int) status)
					.Set(ReduceKeyTypeFields.ReduceKey, reduceKey),
				expectedVersion);

			reduceKeyTypesByView.MultiAdd(writeBatch.Value, CreateKey(view), key);
		}

		public ReduceType GetLastPerformedReduceType(int view, string reduceKey)
		{
            var reduceKeyHash = HashKey(reduceKey);
			var key = CreateKey(view, reduceKey, reduceKeyHash);

			ushort version;
			var value = LoadStruct(tableStorage.ReduceKeyTypes, key, writeBatch.Value, out version);
			if (value == null)
				return ReduceType.None;

			return (ReduceType)value.ReadInt(ReduceKeyTypeFields.ReduceType);
		}

		public IEnumerable<int> GetMappedBuckets(int view, string reduceKey)
		{
            var reduceKeyHash = HashKey(reduceKey);
			var viewAndReduceKey = CreateKey(view, reduceKey, reduceKeyHash);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);

			using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					yield break;

				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);

					yield return value.ReadInt(MappedResultFields.Bucket);
				}
				while (iterator.MoveNext());
			}
		}

		public IEnumerable<MappedResultInfo> GetMappedResults(int view, HashSet<string> keysLeftToReduce, bool loadData, int take, HashSet<string> keysReturned)
		{
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);
			var keysToReduce = new HashSet<string>(keysLeftToReduce);
			foreach (var reduceKey in keysToReduce)
			{
				keysLeftToReduce.Remove(reduceKey);
				
                var reduceKeyHash = HashKey(reduceKey);
                var viewAndReduceKey = CreateKey(view, reduceKey, reduceKeyHash);
				using (var iterator = mappedResultsByViewAndReduceKey.MultiRead(Snapshot, viewAndReduceKey))
				{
					keysReturned.Add(reduceKey);

					if (!iterator.Seek(Slice.BeforeAllKeys))
						continue;

					do
					{
						ushort version;
						var value = LoadStruct(tableStorage.MappedResults, iterator.CurrentKey, writeBatch.Value, out version);
						var size = tableStorage.MappedResults.GetDataSize(Snapshot, iterator.CurrentKey);

						var readReduceKey = value.ReadString(MappedResultFields.ReduceKey);

						yield return new MappedResultInfo
						{
							Bucket = value.ReadInt(MappedResultFields.Bucket),
							ReduceKey = readReduceKey,
							Etag = Etag.Parse(value.ReadBytes(MappedResultFields.Etag)),
							Timestamp = DateTime.FromBinary(value.ReadLong(MappedResultFields.Timestamp)),
							Data = loadData ? LoadMappedResult(iterator.CurrentKey, readReduceKey, mappedResultsData) : null,
							Size = size
						};
					}
					while (iterator.MoveNext());
				}

				if (take < 0)
				{
					yield break;
				}
			}
		}

		private RavenJObject LoadMappedResult(Slice key, string reduceKey, Index dataIndex)
		{
			var read = dataIndex.Read(Snapshot, key, writeBatch.Value);
			if (read == null)
				return null;

			using (var readerStream = read.Reader.AsStream())
			{
				using (var stream = documentCodecs.Aggregate(readerStream, (ds, codec) => codec.Decode(reduceKey, null, ds)))
					return stream.ToJObject();
			}
		}

		public IEnumerable<ReduceTypePerKey> GetReduceKeysAndTypes(int view, int start, int take)
		{
			var reduceKeyTypesByView = tableStorage.ReduceKeyTypes.GetIndex(Tables.ReduceKeyTypes.Indices.ByView);
			using (var iterator = reduceKeyTypesByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys) || !iterator.Skip(start))
					yield break;

				var count = 0;
				do
				{
					ushort version;
					var value = LoadStruct(tableStorage.ReduceKeyTypes, iterator.CurrentKey, writeBatch.Value, out version);

					yield return new ReduceTypePerKey(value.ReadString(ReduceKeyTypeFields.ReduceKey), (ReduceType) value.ReadInt(ReduceKeyTypeFields.ReduceType));

					count++;
				}
				while (iterator.MoveNext() && count < take);
			}
		}

		public void DeleteScheduledReductionForView(int view)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);

			using (var iterator = scheduledReductionsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					ushort version;
					var value = LoadStruct(tableStorage.ScheduledReductions, id, writeBatch.Value, out version);
					if (value == null)
						continue;

					var v = value.ReadInt(ScheduledReductionFields.IndexId);
					var level = value.ReadInt(ScheduledReductionFields.Level);
					var reduceKey = value.ReadString(ScheduledReductionFields.ReduceKey);

					DeleteScheduledReduction(id, v, level, reduceKey);
					storageActionsAccessor.General.MaybePulseTransaction();

				}
				while (iterator.MoveNext());
			}
		}

		public void RemoveReduceResultsForView(int view)
		{
			var reduceResultsByView =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByView);

			using (var iterator = reduceResultsByView.MultiRead(Snapshot, CreateKey(view)))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var id = iterator.CurrentKey.Clone();

					RemoveReduceResult(id);
					storageActionsAccessor.General.MaybePulseTransaction();
				}
				while (iterator.MoveNext());
			}
		}

		private void DeleteScheduledReduction(Slice id, int view, int level, string reduceKey)
		{
		    var reduceKeyHash = HashKey(reduceKey);

			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			var scheduledReductionsByViewAndLevelAndReduceKey = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByViewAndLevelAndReduceKey);

			tableStorage.ScheduledReductions.Delete(writeBatch.Value, id);
			scheduledReductionsByView.MultiDelete(writeBatch.Value, CreateKey(view), id);
			scheduledReductionsByViewAndLevelAndReduceKey.MultiDelete(writeBatch.Value, CreateKey(view, level, reduceKey, reduceKeyHash), id);

		}

		private void DeleteMappedResult(Slice id, int view, string documentId, string reduceKey, string bucket)
		{
		    var reduceKeyHash = HashKey(reduceKey);
			var viewAndDocumentId = CreateKey(view, documentId);
            var viewAndReduceKey = CreateKey(view, reduceKey, reduceKeyHash);
            var viewAndReduceKeyAndSourceBucket = CreateKey(view, reduceKey, reduceKeyHash, bucket);
			var mappedResultsByViewAndDocumentId = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndDocumentId);
			var mappedResultsByView = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByView);
			var mappedResultsByViewAndReduceKey = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKey);
			var mappedResultsByViewAndReduceKeyAndSourceBucket = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.ByViewAndReduceKeyAndSourceBucket);
			var mappedResultsData = tableStorage.MappedResults.GetIndex(Tables.MappedResults.Indices.Data);

			tableStorage.MappedResults.Delete(writeBatch.Value, id);
			mappedResultsByViewAndDocumentId.MultiDelete(writeBatch.Value, viewAndDocumentId, id);
			mappedResultsByView.MultiDelete(writeBatch.Value, CreateKey(view), id);
			mappedResultsByViewAndReduceKey.MultiDelete(writeBatch.Value, viewAndReduceKey, id);
			mappedResultsByViewAndReduceKeyAndSourceBucket.MultiDelete(writeBatch.Value, viewAndReduceKeyAndSourceBucket, id);
			mappedResultsData.Delete(writeBatch.Value, id);
		}

		private void RemoveReduceResult(Slice id)
		{
			ushort version;
			var value = LoadStruct(tableStorage.ReduceResults, id, writeBatch.Value, out version);

			var view = value.ReadInt(ReduceResultFields.IndexId);
			var reduceKey = value.ReadString(ReduceResultFields.ReduceKey);
			var level = value.ReadInt(ReduceResultFields.Level);
			var bucket = value.ReadInt(ReduceResultFields.Bucket);
			var sourceBucket = value.ReadInt(ReduceResultFields.SourceBucket);
		    var reduceKeyHash = HashKey(reduceKey);

            var viewAndReduceKeyAndLevelAndSourceBucket = CreateKey(view, reduceKey, reduceKeyHash, level, sourceBucket);
            var viewAndReduceKeyAndLevel = CreateKey(view, reduceKey, reduceKeyHash, level);
            var viewAndReduceKeyAndLevelAndBucket = CreateKey(view, reduceKey, reduceKeyHash, level, bucket);

			var reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndSourceBucket);
			var reduceResultsByViewAndReduceKeyAndLevel =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevel);
			var reduceResultsByViewAndReduceKeyAndLevelAndBucket =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByViewAndReduceKeyAndLevelAndBucket);
			var reduceResultsByView =
				tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.ByView);
			var reduceResultsData = tableStorage.ReduceResults.GetIndex(Tables.ReduceResults.Indices.Data);

			tableStorage.ReduceResults.Delete(writeBatch.Value, id);
			reduceResultsByViewAndReduceKeyAndLevelAndSourceBucket.MultiDelete(writeBatch.Value, viewAndReduceKeyAndLevelAndSourceBucket, id);
			reduceResultsByViewAndReduceKeyAndLevel.MultiDelete(writeBatch.Value, viewAndReduceKeyAndLevel, id);
			reduceResultsByViewAndReduceKeyAndLevelAndBucket.MultiDelete(writeBatch.Value, viewAndReduceKeyAndLevelAndBucket, id);
			reduceResultsByView.MultiDelete(writeBatch.Value, CreateKey(view), id);
			reduceResultsData.Delete(writeBatch.Value, id);
		}

        private static string HashKey(string key)
        {
            return Convert.ToBase64String(Encryptor.Current.Hash.Compute16(Encoding.UTF8.GetBytes(key)));
        }
	}

    internal class ScheduledReductionDeleter
    {
        private readonly ConcurrentSet<object> innerSet;

        private readonly IDictionary<Slice, object> state = new Dictionary<Slice, object>(new SliceEqualityComparer());

        public ScheduledReductionDeleter(ConcurrentSet<object> set, Func<object, Slice> extractKey)
        {
            innerSet = set;

            InitializeState(set, extractKey);
        }

        private void InitializeState(IEnumerable<object> set, Func<object, Slice> extractKey)
        {
            foreach (var item in set)
            {
                var key = extractKey(item);
                if (key == null)
                    continue;

                state.Add(key, null);
            }
        }

        public bool Delete(Slice key, object value)
        {
            if (state.ContainsKey(key))
                return false;

            state.Add(key, null);
            innerSet.Add(value);

            return true;
        }
    }
}