//-----------------------------------------------------------------------
// <copyright file="RevisionsConfiguration.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net.Http;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Conventions;
using Raven.Client.Http;
using Raven.Client.Json.Serialization;
using Sparrow.Json;

namespace Raven.Client.Documents.Operations.Revisions
{
    public class GetRevisionsOperation<T> : IOperation<RevisionsResult<T>>
    {
        private readonly Parameters _parameters;

        public GetRevisionsOperation(string id)
            : this(new Parameters { Id = id })
        {
        }

        public GetRevisionsOperation(string id, int start, int pageSize)
            : this(new Parameters { Id = id, Start = start, PageSize = pageSize })
        {
        }

        public GetRevisionsOperation(Parameters parameters)
        {
            if (parameters is null)
                throw new ArgumentNullException(nameof(parameters));

            parameters.Validate();

            _parameters = parameters;
        }

        public RavenCommand<RevisionsResult<T>> GetCommand(IDocumentStore store, DocumentConventions conventions, JsonOperationContext context, HttpCache cache)
        {
            return new GetRevisionsResultCommand(_parameters.Id, _parameters.Start, _parameters.PageSize, serialization: store.Conventions.Serialization);
        }

        public class Parameters
        {
            public string Id { get; set; }

            public int? Start { get; set; }

            public int? PageSize { get; set; }

            internal void Validate()
            {
                if (string.IsNullOrEmpty(Id))
                    throw new ArgumentNullException(nameof(Id));
            }
        }

        private class GetRevisionsResultCommand : RavenCommand<RevisionsResult<T>>
        {
            public override bool IsReadRequest => true;

            private readonly ISerializationConventions _serialization;

            private readonly GetRevisionsCommand _cmd;

            public GetRevisionsResultCommand(string id, int? start, int? pageSize, ISerializationConventions serialization)
            {
                _serialization = serialization;
                _cmd = new GetRevisionsCommand(id, start, pageSize);
            }

            public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
            {
                return _cmd.CreateRequest(ctx, node, out url);
            }

            public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null || response.TryGet(nameof(RevisionsResult<T>.Results), out BlittableJsonReaderArray revisions) == false)
                    return;

                response.TryGet(nameof(RevisionsResult<T>.TotalResults), out int total);

                var results = new List<T>(revisions.Length);
                foreach (BlittableJsonReaderObject revision in revisions)
                {
                    if (revision == null)
                        continue;

                    var entity = _serialization.DeserializeEntityFromBlittable<T>(revision);
                    results.Add(entity);
                }

                Result = new RevisionsResult<T>
                {
                    Results = results,
                    TotalResults = total
                };
            }
        }
    }
}
