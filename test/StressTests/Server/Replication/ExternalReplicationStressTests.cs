﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Replication;
using Raven.Server;
using Raven.Server.Config;
using Raven.Tests.Core.Utils.Entities;
using StressTests.Issues;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StressTests.Server.Replication
{
    public class ExternalReplicationStressTests : ReplicationTestBase
    {
        public ExternalReplicationStressTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task TwoWayExternalReplicationShouldNotLoadIdleDatabase()
        {
            using (var server = GetNewServer(new ServerCreationOptions
            {
                CustomSettings = new Dictionary<string, string>
                {
                    [RavenConfiguration.GetKey(x => x.Databases.MaxIdleTime)] = "10",
                    [RavenConfiguration.GetKey(x => x.Databases.FrequencyToCheckForIdle)] = "3",
                    [RavenConfiguration.GetKey(x => x.Core.RunInMemory)] = "false"
                }
            }))
            using (var store1 = GetDocumentStore(new Options
            {
                Server = server,
                RunInMemory = false
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                Server = server,
                RunInMemory = false
            }))
            {
                var externalTask1 = new ExternalReplication(store2.Database, "MyConnectionString1")
                {
                    Name = "MyExternalReplication1"
                };

                var externalTask2 = new ExternalReplication(store1.Database, "MyConnectionString2")
                {
                    Name = "MyExternalReplication2"
                };
                await AddWatcherToReplicationTopology(store1, externalTask1);
                await AddWatcherToReplicationTopology(store2, externalTask2);

                Assert.True(server.ServerStore.DatabasesLandlord.LastRecentlyUsed.TryGetValue(store1.Database, out _));
                Assert.True(server.ServerStore.DatabasesLandlord.LastRecentlyUsed.TryGetValue(store2.Database, out _));

                var now = DateTime.Now;
                var nextNow = now + TimeSpan.FromSeconds(60);
                while (now < nextNow && server.ServerStore.IdleDatabases.Count < 2)
                {
                    await Task.Delay(3000);
                    now = DateTime.Now;
                }
                Assert.Equal(2, server.ServerStore.IdleDatabases.Count);

                await store1.Maintenance.SendAsync(new CreateSampleDataOperation());
                WaitForIndexing(store1);

                var count = 0;
                var docs = store1.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                var replicatedDocs = store2.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                while (docs != replicatedDocs && count < 20)
                {
                    await Task.Delay(3000);
                    replicatedDocs = store2.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                    count++;
                }
                Assert.Equal(docs, replicatedDocs);

                count = 0;
                nextNow = DateTime.Now + TimeSpan.FromMinutes(5);
                while (server.ServerStore.IdleDatabases.Count == 0 && now < nextNow)
                {
                    await Task.Delay(500);
                    if (count % 10 == 0)
                        store1.Maintenance.Send(new GetStatisticsOperation());

                    now = DateTime.Now;
                    count++;
                }
                Assert.Equal(1, server.ServerStore.IdleDatabases.Count);

                nextNow = DateTime.Now + TimeSpan.FromSeconds(15);
                while (now < nextNow)
                {
                    await Task.Delay(2000);
                    store1.Maintenance.Send(new GetStatisticsOperation());
                    Assert.Equal(1, server.ServerStore.IdleDatabases.Count);
                    now = DateTime.Now;
                }

                nextNow = DateTime.Now + TimeSpan.FromMinutes(10);
                while (now < nextNow && server.ServerStore.IdleDatabases.Count < 2)
                {
                    await Task.Delay(3000);
                    now = DateTime.Now;
                }
                Assert.Equal(2, server.ServerStore.IdleDatabases.Count);

                using (var s = store2.OpenSession())
                {
                    s.Advanced.RawQuery<dynamic>("from @all_docs")
                        .ToList();
                }
                Assert.Equal(1, server.ServerStore.IdleDatabases.Count);

                var operation = await store2
                    .Operations
                    .ForDatabase(store2.Database)
                    .SendAsync(new PatchByQueryOperation("from Companies update { this.Name = this.Name + '_patched'; }"));

                await operation.WaitForCompletionAsync();

                nextNow = DateTime.Now + TimeSpan.FromMinutes(2);
                while (now < nextNow && server.ServerStore.IdleDatabases.Count > 0)
                {
                    await Task.Delay(5000);
                    now = DateTime.Now;
                }
                Assert.Equal(0, server.ServerStore.IdleDatabases.Count);

                nextNow = DateTime.Now + TimeSpan.FromMinutes(10);
                while (server.ServerStore.IdleDatabases.Count == 0 && now < nextNow)
                {
                    await Task.Delay(500);
                    if (count % 10 == 0)
                        store2.Maintenance.Send(new GetStatisticsOperation());

                    now = DateTime.Now;
                    count++;
                }
                Assert.Equal(1, server.ServerStore.IdleDatabases.Count);

                nextNow = DateTime.Now + TimeSpan.FromSeconds(15);
                while (now < nextNow)
                {
                    await Task.Delay(2000);
                    store2.Maintenance.Send(new GetStatisticsOperation());
                    Assert.Equal(1, server.ServerStore.IdleDatabases.Count);
                    now = DateTime.Now;
                }
            }
        }

        [Fact]
        public async Task ExternalReplicationShouldNotLoadIdleDatabase()
        {
            using (var server = GetNewServer(new ServerCreationOptions
            {
                CustomSettings = new Dictionary<string, string>
                {
                    [RavenConfiguration.GetKey(x => x.Databases.MaxIdleTime)] = "10",
                    [RavenConfiguration.GetKey(x => x.Databases.FrequencyToCheckForIdle)] = "3",
                    [RavenConfiguration.GetKey(x => x.Replication.RetryMaxTimeout)] = "1",
                    [RavenConfiguration.GetKey(x => x.Core.RunInMemory)] = "false"
                }
            }))
            using (var store1 = GetDocumentStore(new Options
            {
                Server = server,
                RunInMemory = false
            }))
            using (var store2 = GetDocumentStore(new Options
            {
                Server = server,
                RunInMemory = false
            }))
            {
                // lowercase for test
                var externalTask = new ExternalReplication(store2.Database.ToLowerInvariant(), "MyConnectionString")
                {
                    Name = "MyExternalReplication"
                };

                await AddWatcherToReplicationTopology(store1, externalTask);

                Assert.True(server.ServerStore.DatabasesLandlord.LastRecentlyUsed.TryGetValue(store1.Database, out _));
                Assert.True(server.ServerStore.DatabasesLandlord.LastRecentlyUsed.TryGetValue(store2.Database, out _));

                var now = DateTime.Now;
                var nextNow = now + TimeSpan.FromSeconds(60);
                while (now < nextNow && server.ServerStore.IdleDatabases.Count < 2)
                {
                    await Task.Delay(3000);
                    now = DateTime.Now;
                }

                Assert.Equal(2, server.ServerStore.IdleDatabases.Count);

                await store1.Maintenance.SendAsync(new CreateSampleDataOperation());
                WaitForIndexing(store1);

                var count = 0;
                var docs = store1.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                var replicatedDocs = store2.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                while (docs != replicatedDocs && count < 20)
                {
                    await Task.Delay(3000);
                    replicatedDocs = store2.Maintenance.Send(new GetStatisticsOperation()).CountOfDocuments;
                    count++;
                }
                Assert.Equal(docs, replicatedDocs);

                count = 0;
                nextNow = DateTime.Now + TimeSpan.FromMinutes(5);
                while (server.ServerStore.IdleDatabases.Count == 0 && now < nextNow)
                {
                    await Task.Delay(500);
                    if (count % 10 == 0)
                        store1.Maintenance.Send(new GetStatisticsOperation());

                    now = DateTime.Now;
                    count++;
                }
                Assert.Equal(1, server.ServerStore.IdleDatabases.Count);

                nextNow = DateTime.Now + TimeSpan.FromSeconds(15);
                while (now < nextNow)
                {
                    await Task.Delay(2000);
                    store1.Maintenance.Send(new GetStatisticsOperation());
                    Assert.Equal(1, server.ServerStore.IdleDatabases.Count);
                    now = DateTime.Now;
                }
            }
        }

        private List<RavenServer> _nodes;

        [Fact]
        public async Task CanIdleDatabaseInCluster()
        {
            const int clusterSize = 3;
            var databaseName = GetDatabaseName();

            var cluster = await CreateRaftCluster(numberOfNodes: clusterSize, shouldRunInMemory: false, customSettings: new Dictionary<string, string>()
            {
                [RavenConfiguration.GetKey(x => x.Cluster.MoveToRehabGraceTime)] = "1",
                [RavenConfiguration.GetKey(x => x.Cluster.AddReplicaTimeout)] = "1",
                [RavenConfiguration.GetKey(x => x.Cluster.ElectionTimeout)] = "300",
                [RavenConfiguration.GetKey(x => x.Cluster.StabilizationTime)] = "1",
                [RavenConfiguration.GetKey(x => x.Databases.MaxIdleTime)] = "10",
                [RavenConfiguration.GetKey(x => x.Databases.FrequencyToCheckForIdle)] = "3"
            });

            _nodes = cluster.Nodes;

            try
            {
                foreach (var server in _nodes)
                {
                    server.ServerStore.DatabasesLandlord.SkipShouldContinueDisposeCheck = true;
                }

                using (var store = GetDocumentStore(new Options
                {
                    ModifyDatabaseName = s => databaseName,
                    ReplicationFactor = clusterSize,
                    Server = cluster.Leader,
                    RunInMemory = false
                }))
                {
                    var count = RavenDB_13987.WaitForCount(TimeSpan.FromSeconds(300), clusterSize, GetIdleCount);
                    Assert.Equal(clusterSize, count);

                    foreach (var server in _nodes)
                    {
                        Assert.Equal(1, server.ServerStore.IdleDatabases.Count);
                        Assert.True(server.ServerStore.IdleDatabases.TryGetValue(databaseName, out var dictionary));

                        // new incoming replications not saved in IdleDatabases
                        Assert.Equal(0, dictionary.Count);
                    }

                    var rnd = new Random();
                    var index = rnd.Next(0, clusterSize);
                    using (var store2 = new DocumentStore { Urls = new[] { _nodes[index].WebUrl }, Conventions = { DisableTopologyUpdates = true }, Database = databaseName }.Initialize())
                    {
                        await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                    }

                    Assert.Equal(2, GetIdleCount());

                    using (var s = store.OpenAsyncSession())
                    {
                        await s.StoreAsync(new User() { Name = "Egor" }, "foo/bar");
                        await s.SaveChangesAsync();
                    }

                    count = RavenDB_13987.WaitForCount(TimeSpan.FromSeconds(300), 0, GetIdleCount);
                    Assert.Equal(0, count);

                    foreach (var server in _nodes)
                    {
                        using (var store2 = new DocumentStore { Urls = new[] { server.WebUrl }, Conventions = { DisableTopologyUpdates = true }, Database = databaseName }.Initialize())
                        {
                            var docs = (await store.Maintenance.SendAsync(new GetStatisticsOperation())).CountOfDocuments;
                            Assert.Equal(1, docs);
                        }
                    }

                    index = rnd.Next(0, clusterSize);
                    var nextNow = DateTime.Now + TimeSpan.FromSeconds(300);
                    var now = DateTime.Now;
                    using (var store2 = new DocumentStore { Urls = new[] { _nodes[index].WebUrl }, Conventions = { DisableTopologyUpdates = true }, Database = databaseName }.Initialize())
                    {
                        while (now < nextNow && GetIdleCount() < 2)
                        {
                            await Task.Delay(2000);
                            await store2.Maintenance.SendAsync(new GetStatisticsOperation());
                            now = DateTime.Now;
                        }
                    }
                    Assert.Equal(2, GetIdleCount());
                }
            }
            finally
            {
                foreach (var server in _nodes)
                {
                    server.ServerStore.DatabasesLandlord.SkipShouldContinueDisposeCheck = false;
                }
            }
        }

        private int GetIdleCount()
        {
            return _nodes.Sum(server => server.ServerStore.IdleDatabases.Count);
        }
    }
}
