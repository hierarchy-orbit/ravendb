﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Exceptions.Database;
using Raven.Client.Json.Converters;
using Raven.Client.Server;
using Raven.Client.Util;
using Raven.Server.Config;
using Raven.Server.NotificationCenter.Notifications.Server;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow;

namespace Raven.Server.Documents
{
    public class DatabasesLandlord : AbstractLandlord<DocumentDatabase>
    {
        public event Action<string> OnDatabaseLoaded = delegate { };


        public override Task<DocumentDatabase> TryGetOrCreateResourceStore(StringSegment databaseName)
        {
            if (HasLocks != 0)
            {
                AssertLocks(databaseName);
            }
            Task<DocumentDatabase> database;
            if (ResourcesStoresCache.TryGetValue(databaseName, out database))
            {
                if (database.IsFaulted || database.IsCanceled)
                {
                    ResourcesStoresCache.TryRemove(databaseName, out database);
                    DateTime time;
                    LastRecentlyUsed.TryRemove(databaseName, out time);
                    // and now we will try creating it again
                }
                else
                {
                    return database;
                }
            }

            return CreateDatabase(databaseName);
        }

        private Task<DocumentDatabase> CreateDatabase(StringSegment databaseName)
        {
            var config = CreateDatabaseConfiguration(databaseName);
            if (config == null)
                return null;

            if (!ResourceSemaphore.Wait(ConcurrentResourceLoadTimeout))
                throw new DatabaseConcurrentLoadTimeoutException(
                    "Too much databases loading concurrently, timed out waiting for them to load.");
            try
            {
                var task = new Task<DocumentDatabase>(() => ActuallyCreateDatabase(databaseName, config));

                var database = ResourcesStoresCache.GetOrAdd(databaseName, task);
                if (database == task)
                {
                    task.Start(); // the semaphore will be released here at the end of the task
                }
                else
                {
                    // the other task will release it
                    ResourceSemaphore.Release();
                }

                return database;
            }
            catch (Exception)
            {
                ResourceSemaphore.Release();
                throw;
            }
        }

        private DocumentDatabase ActuallyCreateDatabase(StringSegment databaseName, RavenConfiguration config)
        {
            try
            {
                var db = CreateDocumentsStorage(databaseName, config);
                ServerStore.NotificationCenter.Add(
                    ResourceChanged.Create(Constants.Documents.Prefix + databaseName,
                        ResourceChangeType.Load));
                return db;
            }
            catch (Exception e)
            {
                // if we are here, there is an error, and if there is an error, we need to clear it from the 
                // resource store cache so we can try to reload it.
                // Note that we return the faulted task anyway, because we need the user to look at the error
                if (e.Data.Contains("Raven/KeepInResourceStore") == false)
                {
                    Task<DocumentDatabase> val;
                    ResourcesStoresCache.TryRemove(databaseName, out val);
                }
                throw;
            }
            finally
            {
                try
                {
                    ResourceSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void AssertLocks(string databaseName)
        {
            if (Locks.Contains(DisposingLock))
                throw new ObjectDisposedException("DatabaseLandlord", "Server is shutting down, can't access any databases");

            if (Locks.Contains(databaseName))
                throw new InvalidOperationException($"Database '{databaseName}' is currently locked and cannot be accessed.");

        }

        private DocumentDatabase CreateDocumentsStorage(StringSegment databaseName, RavenConfiguration config)
        {
            try
            {
                var sp = Stopwatch.StartNew();
                var documentDatabase = new DocumentDatabase(config.ResourceName, config, ServerStore);
                documentDatabase.Initialize();
                DeleteDatabaseCachedInfo(documentDatabase, ServerStore);
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Started database {config.ResourceName} in {sp.ElapsedMilliseconds:#,#;;0}ms");

                OnDatabaseLoaded(config.ResourceName);

                // if we have a very long init process, make sure that we reset the last idle time for this db.
                LastRecentlyUsed.AddOrUpdate(databaseName, SystemTime.UtcNow, (_, time) => SystemTime.UtcNow);
                return documentDatabase;
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to start database {config.ResourceName}", e);
                throw new DatabaseLoadFailureException($"Failed to start database {config.ResourceName}" + Environment.NewLine + 
                                                       $"At {config.Core.DataDirectory}" , e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteDatabaseCachedInfo(DocumentDatabase database, ServerStore serverStore)
        {
            serverStore.DatabaseInfoCache.Delete(database.Name);
        }

        public RavenConfiguration CreateDatabaseConfiguration(StringSegment databaseName, bool ignoreDisabledDatabase = false)
        {
            if (databaseName.IsNullOrWhiteSpace())
                throw new ArgumentNullException(nameof(databaseName), "Database name cannot be empty");
            if (databaseName.Equals("<system>")) // This is here to guard against old ravendb tests
                throw new ArgumentNullException(nameof(databaseName), "Database name cannot be <system>. Using of <system> database indicates outdated code that was targeted RavenDB 3.5.");

            var document = GetDatabaseDocument(databaseName, ignoreDisabledDatabase);
            if (document == null)
                return null;

            return CreateConfiguration(databaseName, document, RavenConfiguration.GetKey(x => x.Core.DataDirectory));
        }

        protected RavenConfiguration CreateConfiguration(StringSegment databaseName, DatabaseDocument document, string folderPropName)
        {
            var config = RavenConfiguration.CreateFrom(ServerStore.Configuration, databaseName, ResourceType.Database);

            foreach (var setting in document.Settings)
            {
                config.SetSetting(setting.Key, setting.Value);
            }
            Unprotect(document);

            foreach (var securedSetting in document.SecuredSettings)
            {
                config.SetSetting(securedSetting.Key, securedSetting.Value);
            }

            config.Initialize();
            config.CopyParentSettings(ServerStore.Configuration);
            return config;
        }

        public void Unprotect(DatabaseDocument databaseDocument)
        {
            if (databaseDocument.SecuredSettings == null)
            {
                databaseDocument.SecuredSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            foreach (var prop in databaseDocument.SecuredSettings.ToList())
            {
                if (prop.Value == null)
                    continue;
                var bytes = Convert.FromBase64String(prop.Value);
                var entrophy = Encoding.UTF8.GetBytes(prop.Key);
                try
                {
                    /*var unprotectedValue = ProtectedData.Unprotect(bytes, entrophy, DataProtectionScope.CurrentUser);
                    databaseDocument.SecuredSettings[prop.Key] = Encoding.UTF8.GetString(unprotectedValue);*/
                }
                catch (Exception e)
                {
                    if (Logger.IsInfoEnabled)
                        Logger.Info("Could not unprotect secured db data " + prop.Key + " setting the value to '<data could not be decrypted>'", e);
                    databaseDocument.SecuredSettings[prop.Key] = Constants.Documents.Encryption.DataCouldNotBeDecrypted;
                }
            }
        }

        private DatabaseDocument GetDatabaseDocument(StringSegment databaseName, bool ignoreDisabledDatabase = false)
        {
            // We allocate the context here because it should be relatively rare operation
            TransactionOperationContext context;
            using (ServerStore.ContextPool.AllocateOperationContext(out context))
            {
                context.OpenReadTransaction();

                var dbId = Constants.Documents.Prefix + databaseName;
                var jsonReaderObject = ServerStore.Read(context, dbId);
                if (jsonReaderObject == null)
                    return null;

                var document = JsonDeserializationClient.DatabaseDocument(jsonReaderObject);
                
                if (document.Disabled && !ignoreDisabledDatabase)
                    throw new DatabaseDisabledException("The database has been disabled.");

                return document;
            }
        }

        public DatabasesLandlord(ServerStore serverStore) : base(serverStore)
        {

        }

        public override DateTime LastWork(DocumentDatabase resource)
        {
            // This allows us to increase the time large databases will be held in memory
            // Using this method, we'll add 0.5 ms per KB, or roughly half a second of idle time per MB.

            var envs = resource.GetAllStoragesEnvironment();

            long dbSize = 0;
            var maxLastWork = DateTime.MinValue;

            foreach (var env in envs)
            {
                dbSize += env.Environment.Stats().AllocatedDataFileSizeInBytes;

                if (env.Environment.LastWorkTime > maxLastWork)
                    maxLastWork = env.Environment.LastWorkTime;
            }

            return maxLastWork + TimeSpan.FromMilliseconds(dbSize / 1024L);
        }
    }
}