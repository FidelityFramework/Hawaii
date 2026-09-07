# Group clients around one shared model

Large API descriptions often need several client projects while retaining one
definition of each schema type. For the .NET target, `clientGroups` assigns
selected operations to explicit projects:

```json
{
  "schema": "api.json",
  "output": "Generated",
  "project": "Example.Api.Core",
  "target": "fsharp",
  "asyncReturnType": "task",
  "clientGroups": [
    { "project": "Example.Api.Catalog", "operationIds": ["ListItems", "CreateItem"] },
    { "project": "Example.Api.Accounts", "operationIds": ["GetAccount"] }
  ]
}
```

`project` owns the shared schema types, operation response types, and HTTP
support. Each group produces a client project in a subdirectory bearing its
project name. The group projects reference the shared project; they do not
copy its type definitions or reference one another. A group's final project
name supplies its client class name, such as `CatalogClient`.

Every operation selected by the input and `filterTags` must belong to exactly
one group. Operation IDs must be unique and nonempty. Unknown, repeated, or
omitted IDs are errors, as are duplicate project names or a group that shares
the model project's name. This validation occurs before the output directory
is changed. `client-groups.json` records the resulting project and operation
ownership for downstream tooling.

The calling project decides the taxonomy. Hawaii does not infer a grouping
from service brands or overlapping OpenAPI tags. Deprecation does not remove
an operation from the partition.

Omitting `clientGroups`, or using an empty list, retains the single-project
layout. Grouped output currently supports `target: "fsharp"`; the generator
rejects grouped Fable output until its separate serializer and project boundary
are covered by equivalent tests.
