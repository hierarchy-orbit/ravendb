﻿// -----------------------------------------------------------------------
//  <copyright file="Lists.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Raven.Database.Extensions;
using Raven.Database.Storage;
using Raven.Json.Linq;
using Raven.Abstractions.Extensions;

namespace Raven.Storage.Esent.StorageActions
{
	public partial class DocumentStorageActions : IListsStorageActions
	{
		public void Add(string name, string key, RavenJObject data)
		{
			using (var update = new Update(session, Lists, JET_prep.Insert))
			{
				Api.SetColumn(session, Lists, tableColumnsCache.ListsColumns["name"], name, Encoding.Unicode);
				Api.SetColumn(session, Lists, tableColumnsCache.ListsColumns["key"], key, Encoding.Unicode);
				Api.SetColumn(session, Lists, tableColumnsCache.ListsColumns["etag"], uuidGenerator.CreateSequentialUuid().TransformToValueForEsentSorting());
				using (Stream stream = new BufferedStream(new ColumnStream(session, Lists, tableColumnsCache.ListsColumns["data"])))
				{
					data.WriteTo(stream);
					stream.Flush();
				}
				update.Save();
			}
		}

		public void Remove(string name, string key)
		{
			Api.JetSetCurrentIndex(session, Lists, "by_name_and_key");
			Api.MakeKey(session, Lists, name, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, Lists, key, Encoding.Unicode, MakeKeyGrbit.None);

			if(Api.TrySeek(session, Lists, SeekGrbit.SeekEQ))
				Api.JetDelete(session, Lists);
		}

		public IEnumerable<Tuple<Guid, RavenJObject>> Read(string name, Guid start, int take)
		{
			Api.JetSetCurrentIndex(session, Lists, "by_name_and_etag");
			Api.MakeKey(session, Lists, name, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, Lists, start.TransformToValueForEsentSorting(), MakeKeyGrbit.None);
			if (Api.TrySeek(session, Lists, SeekGrbit.SeekGT) == false)
				yield break;
			int count = 0;
			do
			{
				var nameFromDb = Api.RetrieveColumnAsString(session, Lists, tableColumnsCache.ListsColumns["name"], Encoding.Unicode);
				if(string.Equals(name, nameFromDb, StringComparison.InvariantCultureIgnoreCase) == false)
					yield break;

				count++;

				var etag = Api.RetrieveColumn(session, Lists, tableColumnsCache.ListsColumns["etag"]).TransfromToGuidWithProperSorting();
				using (Stream stream = new BufferedStream(new ColumnStream(session, Lists, tableColumnsCache.ListsColumns["data"])))
				{
					yield return Tuple.Create(etag, stream.ToJObject());
				}
			} while (Api.TryMoveNext(session, Lists) && count < take);
		
		}

		public RavenJObject Read(string name, string key)
		{
			Api.JetSetCurrentIndex(session, Lists, "by_name_and_key");
			Api.MakeKey(session, Lists, name, Encoding.Unicode, MakeKeyGrbit.NewKey);
			Api.MakeKey(session, Lists, key, Encoding.Unicode, MakeKeyGrbit.None);

			if (Api.TrySeek(session, Lists, SeekGrbit.SeekEQ) == false)
				return null;

			using (Stream stream = new BufferedStream(new ColumnStream(session, Lists, tableColumnsCache.ListsColumns["data"])))
			{
				return stream.ToJObject();
			}
		}
	}
}