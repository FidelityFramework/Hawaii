# Clients with a shared model

```sh
python3 tests/client-groups/run.py
```

The fixture assigns three operations to two client projects. One client reads
a shared payload and passes that same F# value directly to the other client's
request method. A fake HTTP handler checks the actual paths and payload. The
consumer also checks the model's assembly owner and verifies that both client
assemblies depend only on the shared project within the generated project set.
The test runs with task and async clients in Debug and Release.

Reordering the group configuration must produce identical F# source and project
files. Invalid partitions, including missing or repeated operations and project
name collisions, must fail before changing an existing output directory.
Deprecated operations remain part of the partition unless the input consumer
has explicitly removed them.

Use `--assembly /path/to/Hawaii.dll` to test a frozen generator build and
`--work-directory /empty/path` to retain all generation and compilation logs.
