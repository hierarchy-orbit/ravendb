﻿using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FastTests;
using Newtonsoft.Json;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Documents;
using Raven.Server.Documents.PeriodicBackup.Azure;
using Raven.Server.Documents.PeriodicBackup.Restore;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.PeriodicBackup.Restore
{
    public class RestoreFromAzure : RavenTestBase
    {
        public RestoreFromAzure(ITestOutputHelper output) : base(output)
        {
        }

        [Fact, Trait("Category", "Smuggler")]
        public void restore_azure_cloud_settings_tests()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                var databaseName = $"restored_database-{Guid.NewGuid()}";
                var restoreConfiguration = new RestoreFromAzureConfiguration
                {
                    DatabaseName = databaseName
                };

                var restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);

                var e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountKey)} and {nameof(AzureSettings.SasToken)} cannot be both null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.AccountKey = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountName)} cannot be null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.AccountName = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.StorageContainer)} cannot be null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.StorageContainer = "test";
                restoreConfiguration.Settings.AccountKey = null;
                restoreConfiguration.Settings.SasToken = "testSasToken";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.SasToken)} isn't in the correct format", e.InnerException.Message);

                restoreConfiguration.Settings.AccountKey = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountKey)} and {nameof(AzureSettings.SasToken)} cannot be used simultaneously", e.InnerException.Message);
            }
        }

        [AzureStorageEmulatorFact]
        public void can_backup_and_restore()
        {
            var azureSettings = GenerateAzureSettings();
            InitContainer(azureSettings);

            using (var store = GetDocumentStore())
            {

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "oren" }, "users/1");
                    session.CountersFor("users/1").Increment("likes", 100);
                    session.SaveChanges();
                }

                var config = new PeriodicBackupConfiguration
                {
                    BackupType = BackupType.Backup,
                    AzureSettings = azureSettings,
                    IncrementalBackupFrequency = "0 0 1 1 *"
                };

                var backupTaskId = (store.Maintenance.Send(new UpdatePeriodicBackupOperation(config))).TaskId;
                store.Maintenance.Send(new StartBackupOperation(true, backupTaskId));
                var operation = new GetPeriodicBackupStatusOperation(backupTaskId);

                PeriodicBackupStatus status = null;
                var value = WaitForValue(() =>
                {
                    status = store.Maintenance.Send(operation).Status;
                    return status?.LastEtag;
                }, 4);
                Assert.True(4 == value, $"4 == value, Got status: {status != null}, exception: {status?.Error?.Exception}");
                Assert.True(status.LastOperationId != null, $"status.LastOperationId != null, Got status: {status != null}, exception: {status?.Error?.Exception}");

                OperationState backupOperation = null;
                var operationStatus = WaitForValue(() =>
                {
                    backupOperation = store.Maintenance.Send(new GetOperationStateOperation(status.LastOperationId.Value));
                    return backupOperation.Status;
                }, OperationStatus.Completed);
                Assert.Equal(OperationStatus.Completed, operationStatus);

                var backupResult = backupOperation.Result as BackupResult;
                Assert.NotNull(backupResult);
                Assert.True(backupResult.Counters.Processed, "backupResult.Counters.Processed");
                Assert.Equal(1, backupResult.Counters.ReadCount);

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "ayende" }, "users/2");
                    session.CountersFor("users/2").Increment("downloads", 200);

                    session.SaveChanges();
                }

                var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                store.Maintenance.Send(new StartBackupOperation(false, backupTaskId));
                value = WaitForValue(() => store.Maintenance.Send(operation).Status.LastEtag, lastEtag);
                Assert.Equal(lastEtag, value);

                // restore the database with a different name
                var databaseName = $"restored_database-{Guid.NewGuid()}";

                azureSettings.RemoteFolderName = status.FolderName;
                var restoreFromGoogleCloudConfiguration = new RestoreFromAzureConfiguration()
                {
                    DatabaseName = databaseName,
                    Settings = azureSettings,
                    DisableOngoingTasks = true
                };
                var googleCloudOperation = new RestoreBackupOperation(restoreFromGoogleCloudConfiguration);
                var restoreOperation = store.Maintenance.Server.Send(googleCloudOperation);

                restoreOperation.WaitForCompletion(TimeSpan.FromSeconds(30));
                using (var store2 = GetDocumentStore(new Options()
                {
                    CreateDatabase = false,
                    ModifyDatabaseName = s => databaseName
                }))
                {
                    using (var session = store2.OpenSession(databaseName))
                    {
                        var users = session.Load<User>(new[] { "users/1", "users/2" });
                        Assert.True(users.Any(x => x.Value.Name == "oren"));
                        Assert.True(users.Any(x => x.Value.Name == "ayende"));

                        var val = session.CountersFor("users/1").Get("likes");
                        Assert.Equal(100, val);
                        val = session.CountersFor("users/2").Get("downloads");
                        Assert.Equal(200, val);
                    }

                    var originalDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).Result;
                    var restoredDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                    using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                        Assert.Contains($"A:7-{originalDatabase.DbBase64Id}", databaseChangeVector);
                        Assert.Contains($"A:8-{restoredDatabase.DbBase64Id}", databaseChangeVector);
                    }
                }
            }
        }

        [AzureStorageEmulatorFact]
        public async Task can_create_azure_snapshot_and_restore_using_restore_point()
        {
            var azureSettings = GenerateAzureSettings();
            InitContainer(azureSettings);
            azureSettings.RemoteFolderName = "testFolder";

            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "oren" }, "users/1");
                    session.CountersFor("users/1").Increment("likes", 100);
                    session.SaveChanges();
                }

                var config = new PeriodicBackupConfiguration
                {
                    BackupType = BackupType.Snapshot,
                    AzureSettings = azureSettings,
                    IncrementalBackupFrequency = "0 0 1 1 *"
                };

                var backupTaskId = (store.Maintenance.Send(new UpdatePeriodicBackupOperation(config))).TaskId;
                store.Maintenance.Send(new StartBackupOperation(true, backupTaskId));
                var operation = new GetPeriodicBackupStatusOperation(backupTaskId);
                PeriodicBackupStatus status = null;
                var value = WaitForValue(() =>
                {
                    status = store.Maintenance.Send(operation).Status;
                    return status?.LastEtag;
                }, 4);
                Assert.True(4 == value, $"4 == value, Got status: {status != null}, exception: {status?.Error?.Exception}");
                Assert.True(status.LastOperationId != null, $"status.LastOperationId != null, Got status: {status != null}, exception: {status?.Error?.Exception}");

                var client = store.GetRequestExecutor().HttpClient;
                var data = new StringContent(JsonConvert.SerializeObject(azureSettings), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(store.Urls.First() + "/admin/restore/points?type=Azure ", data);
                string result = response.Content.ReadAsStringAsync().Result;
                var restorePoints = JsonConvert.DeserializeObject<RestorePoints>(result);
                Assert.Equal(1, restorePoints.List.Count);
                var point = restorePoints.List.First();

                var databaseName = $"restored_database-{Guid.NewGuid()}";
                azureSettings.RemoteFolderName = azureSettings.RemoteFolderName + "/" + status.FolderName;
                var restoreOperation = await store.Maintenance.Server.SendAsync(new RestoreBackupOperation(new RestoreFromAzureConfiguration()
                {
                    DatabaseName = databaseName,
                    Settings = azureSettings,
                    DisableOngoingTasks = true,
                    LastFileNameToRestore = point.FileName,
                }));

                await restoreOperation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                using (var store2 = GetDocumentStore(new Options() { CreateDatabase = false, ModifyDatabaseName = s => databaseName }))
                {
                    using (var session = store2.OpenSession(databaseName))
                    {
                        var users = session.Load<User>(new[] { "users/1", "users/2" });
                        Assert.True(users.Any(x => x.Value.Name == "oren"));

                        var val = session.CountersFor("users/1").Get("likes");
                        Assert.Equal(100, val);
                    }

                    var originalDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).Result;
                    var restoredDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                    using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                    using (ctx.OpenReadTransaction())
                    {
                        var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                        Assert.Equal($"A:4-{originalDatabase.DbBase64Id}", databaseChangeVector);
                    }
                }
            }
        }

        private void InitContainer(AzureSettings azureSettings)
        {
            using (var client = new RavenAzureClient(azureSettings))
            {
                client.DeleteContainer();
                client.PutContainer();
            }
        }

        private AzureSettings GenerateAzureSettings(string containerName = "mycontainer")
        {
            return new AzureSettings
            {
                AccountName = Azure.AzureAccountName,
                AccountKey = Azure.AzureAccountKey,
                StorageContainer = containerName,
                RemoteFolderName = ""
            };
        }
    }
}
