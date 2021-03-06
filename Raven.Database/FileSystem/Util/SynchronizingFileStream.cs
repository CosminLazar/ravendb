﻿using System.Collections.Specialized;
using System.Security.Cryptography;
using Raven.Abstractions.Util.Encryptors;
using Raven.Database.Extensions;
using Raven.Database.FileSystem.Extensions;
using Raven.Database.FileSystem.Infrastructure;
using Raven.Database.FileSystem.Search;
using Raven.Database.FileSystem.Storage;
using Raven.Database.FileSystem.Storage.Esent;
using Raven.Json.Linq;

namespace Raven.Database.FileSystem.Util
{
	public class SynchronizingFileStream : StorageStream
	{
		private readonly IHashEncryptor md5Hasher;

		private SynchronizingFileStream(ITransactionalStorage transactionalStorage, string fileName,
										StorageStreamAccess storageStreamAccess, RavenJObject metadata,
										IndexStorage indexStorage, StorageOperationsTask operations)
			: base(transactionalStorage, fileName, storageStreamAccess, metadata, indexStorage, operations)
		{
		    md5Hasher = Encryptor.Current.CreateHash();
		}

		public bool PreventUploadComplete { get; set; }

		public string FileHash { get; private set; }

		public override void Flush()
		{
			if (InnerBuffer != null && InnerBufferOffset > 0)
			{
				md5Hasher.TransformBlock(InnerBuffer, 0, InnerBufferOffset);
				base.Flush();
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (!PreventUploadComplete)
			{
				base.Dispose(disposing);

			    FileHash = IOExtensions.GetMD5Hex(md5Hasher.TransformFinalBlock());
				md5Hasher.Dispose();
			}
		}

		public static SynchronizingFileStream CreatingOrOpeningAndWriting(ITransactionalStorage storage, IndexStorage search,
																		   StorageOperationsTask operationsTask,
																		   string fileName, RavenJObject metadata)
		{
			return new SynchronizingFileStream(storage, fileName, StorageStreamAccess.CreateAndWrite, metadata, search, operationsTask) { PreventUploadComplete = true };
		}
	}
}