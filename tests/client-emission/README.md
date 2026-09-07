# Client emission regressions

Run `python3 tests/client-emission/run.py` from the Hawaii checkout. The runner builds Hawaii, generates a small OpenAPI client for both F# task and async return modes, then compiles and runs each in Debug and Release. Use `--assembly /path/to/Hawaii.dll` to test an already built generator.

The fake HTTP handler checks the outgoing URI and supplies responses locally. Coverage includes parameters named `task` and `async`, status ranges and default responses without payloads, preservation of the actual HTTP status, and a deprecated operation that remains callable. Deprecation alone does not establish retirement; an input-selection policy may explicitly exclude retired operations before generation.

Generation and compiler logs remain in the printed work directory. A failing generated client is a failed test; the runner does not rewrite generated F#.
