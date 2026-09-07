#!/usr/bin/env python3
"""Verify generated multipart requests against a real loopback HTTP listener."""
import argparse
from email.parser import BytesParser
from email.policy import default
import http.server
import json
from pathlib import Path
import subprocess
import tempfile
import threading
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def run(command, work, log, expected=0):
    result = subprocess.run(command, cwd=work, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.write_text(result.stdout)
    if result.returncode != expected:
        raise RuntimeError(f"Unexpected exit {result.returncode}; see {log}\n{result.stdout}")
    return result.stdout


def assert_requests(requests):
    assert len(requests) == 8, f"Expected eight HTTP requests, got {len(requests)}"
    binary = bytes([0, 255, 128, 65, 13, 10])
    wasm = bytes([0, 97, 115, 109, 255])
    first = requests[0]
    assert first["path"] == "/upload"
    parts = first["parts"]
    files = [part for part in parts if part["name"] == "files"]
    assert [(part["filename"], part["media"], part["bytes"]) for part in files] == [
        ("first.bin", "application/octet-stream", binary), ("second.wasm", "application/wasm", wasm)]
    single = next(part for part in parts if part["name"] == "single")
    assert (single["filename"], single["media"], single["bytes"]) == ("image.png", "image/png", bytes([137, 80, 78, 71, 0, 255]))
    assert [(part["media"], part["bytes"]) for part in parts if part["name"] == "labels"] == [("text/plain", b"one"), ("text/plain", b"two")]
    meta = next(part for part in parts if part["name"] == "metadata")
    assert meta["media"] == "application/json" and meta["filename"] is None
    assert json.loads(meta["bytes"]) == {"entry": "first.bin", "annotations": {"label": "kept"}, "future": {"enabled": True}}
    items = [part for part in parts if part["name"] == "metadataItems"]
    assert [json.loads(part["bytes"]) for part in items] == [{"label": "first"}, {"label": "second"}]
    assert all(part["media"] == "application/json" for part in items)
    untyped = next(part for part in parts if part["name"] == "untyped")
    assert untyped["media"] == "application/json" and json.loads(untyped["bytes"]) == {"message": "inline", "extra": [1, 2]}
    dynamic = requests[1]["parts"]
    assert len(dynamic) == 2
    meta = next(part for part in dynamic if part["name"] == "metadata")
    file = next(part for part in dynamic if part["name"] == "main.wasm")
    assert json.loads(meta["bytes"])["entry"] == file["name"]
    assert (file["filename"], file["media"], file["bytes"]) == ("module.wasm", "application/wasm", wasm)
    assert [part["name"] for part in requests[2]["parts"]] == ["metadata"]
    assert requests[3]["path"] == "/named"
    assert json.loads(requests[3]["parts"][0]["bytes"]) == {"label": "canonical"}
    assert requests[3]["parts"][0]["media"] == "application/json"

    assert requests[4]["path"] == "/legacy"
    assert [(part["name"], part["filename"], part["media"], part["bytes"]) for part in requests[4]["parts"]] == [("files", None, "application/octet-stream", binary), ("files", None, "application/octet-stream", wasm)]

    assert requests[5] == {"path": "/empty", "parts": []}
    for request, media, payload in [(requests[6], "text/plain", "Hello λ".encode()), (requests[7], "application/octet-stream", binary)]:
        assert request["path"] == "/union"
        value = next(part for part in request["parts"] if part["name"] == "value")
        assert (value["media"], value["filename"], value["bytes"]) == (media, None, payload)
        metadata = next(part for part in request["parts"] if part["name"] == "metadata")
        assert metadata["media"] == "application/json"
        assert json.loads(metadata["bytes"]) == {"future": [True, 12, {"nested": "retained"}], "unknown": None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path)
    parser.add_argument("--work-directory", type=Path)
    args = parser.parse_args()
    work = (args.work_directory or Path(tempfile.mkdtemp(prefix="hawaii-multipart-requests-"))).resolve()
    if work.exists() and any(work.iterdir()):
        raise ValueError(f"Work directory is not empty: {work}")
    work.mkdir(parents=True, exist_ok=True)
    if args.assembly:
        assembly = args.assembly.resolve()
    else:
        run(["dotnet", "build", "src/Hawaii.fsproj", "--nologo"], ROOT, work / "tool-build.log")
        assembly = ROOT / "src/bin/Debug/net10.0/Hawaii.dll"
    config = {"schema": str(HERE / "openapi.json"), "project": "Hawaii.MultipartRegression", "output": str(work / "Generated"),
              "target": "fsharp", "asyncReturnType": "task", "emptyDefinitions": "free-form",
              "preserveJsonSchemas": ["ArbitraryMetadata", "TextOrBytes"]}
    config_path = work / "hawaii.json"
    config_path.write_text(json.dumps(config, indent=2) + "\n")
    run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], ROOT, work / "generate.log")
    runtime = work / "Runtime"
    runtime.mkdir()
    (runtime / "Runtime.fsproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '</PropertyGroup><ItemGroup><ProjectReference Include="../Generated/Hawaii.MultipartRegression.fsproj" />'
        f'<Compile Include="{escape(str(HERE / "Runtime.fs"), {chr(34): "&quot;"})}" /></ItemGroup></Project>\n')
    requests = []
    phase = ["debug"]

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(int(self.headers["Content-Length"]))
            (work / f"request-{phase[0]}-{len(requests)}.bin").write_bytes(body)
            message = BytesParser(policy=default).parsebytes(b"Content-Type: " + self.headers["Content-Type"].encode() + b"\r\nMIME-Version: 1.0\r\n\r\n" + body)
            assert message.get_content_type() == "multipart/form-data", self.headers["Content-Type"]
            assert message.get_boundary() and body.strip().endswith(b"--" + message.get_boundary().encode() + b"--")
            requests.append({"path": self.path, "parts": [{"name": part.get_param("name", header="Content-Disposition"),
                "filename": part.get_filename(), "media": part.get_content_type(), "bytes": part.get_payload(decode=True)} for part in message.iter_parts()]})
            self.send_response(204)
            self.end_headers()

        def log_message(self, *_):
            pass

    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            for configuration in ["Debug", "Release"]:
                phase[0] = configuration.lower()
                requests.clear()
                print(run(["dotnet", "run", "--configuration", configuration, "--project", str(runtime / "Runtime.fsproj"), "--", f"http://127.0.0.1:{server.server_port}"], ROOT, work / f"runtime-{phase[0]}.log").strip())
                assert_requests(requests)
        finally:
            server.shutdown()
            thread.join()
    # allOf intersections may refine a field but may not silently replace its wire kind.
    rejected = json.loads((HERE / "openapi.json").read_text())
    rejected["paths"]["/upload"]["post"]["requestBody"]["content"]["multipart/form-data"]["schema"]["allOf"][1]["properties"]["files"] = {"type": "integer"}
    bad_schema = work / "conflicting.json"
    bad_schema.write_text(json.dumps(rejected))
    config.update(schema=str(bad_schema), output=str(work / "Rejected"))
    config_path.write_text(json.dumps(config))
    output = run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], ROOT, work / "conflicting.log", expected=1)
    assert "Conflicting multipart allOf property types" in output
    assert not (work / "Rejected").exists()
    print(f"Multipart byte/name/media contracts and conflicting intersections verified. Logs: {work}")


if __name__ == "__main__":
    main()
