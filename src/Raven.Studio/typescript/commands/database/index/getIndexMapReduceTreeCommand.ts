import commandBase = require("commands/commandBase");
import database = require("models/resources/database");
import endpoints = require("endpoints");

class getIndexMapReduceTreeCommand extends commandBase {

    constructor(private db: database, private indexName: string, private documentIds: Array<string>) {
        super();
    }

    execute(): JQueryPromise<Raven.Server.Documents.Indexes.Debugging.ReduceTree[]> {
        const url = endpoints.databases.index.indexesDebug;
        const args =
        {
            docId: this.documentIds,
            name: this.indexName,
            op: "map-reduce-tree"
        };
        return this.query(url + this.urlEncodeArgs(args), null, this.db, x => x.Results)
            .fail((response: JQueryXHR) => this.reportError("Failed to load map reduce tree", response.responseText, response.statusText));
    }
} 

export = getIndexMapReduceTreeCommand;
