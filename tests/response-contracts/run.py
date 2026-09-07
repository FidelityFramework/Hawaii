#!/usr/bin/env python3
"""Generate, compile, and run HTTP contract regressions without network requests."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def run(command, directory, log):
    result = subprocess.run(command, cwd=directory, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {' '.join(map(str, command))}\n{result.stdout}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-directory", type=Path)
    parser.add_argument("--assembly", type=Path, help="Use a frozen tool assembly instead of building shared source")
    args = parser.parse_args()
    work = (args.work_directory or Path(tempfile.mkdtemp(prefix="hawaii-response-contracts-"))).resolve()
    if work.exists() and any(work.iterdir()):
        raise ValueError(f"Work directory is not empty: {work}")
    work.mkdir(parents=True, exist_ok=True)
    if args.assembly is None:
        run(["dotnet", "build", "src/Hawaii.fsproj", "--nologo"], ROOT, work / "tool-build.log")
    assembly = (args.assembly or ROOT / "src/bin/Debug/net10.0/Hawaii.dll").resolve()
    print(run(["dotnet", "fsi", f"--reference:{assembly}",
               f"--reference:{assembly.parent / 'Newtonsoft.Json.dll'}", str(HERE / "Simplification.fsx")],
              ROOT, work / "simplification.log").strip())
    print(run(["dotnet", "fsi", f"--reference:{assembly}",
               f"--reference:{assembly.parent / 'Newtonsoft.Json.dll'}", str(HERE / "InlineSchemas.fsx")],
              ROOT, work / "inline-schemas.log").strip())
    generated = work / "Generated"
    config = {"schema": str(HERE / "status-openapi.json"), "project": "Hawaii.StatusRegression",
              "output": str(generated), "target": "fsharp", "asyncReturnType": "task", "emptyDefinitions": "free-form",
              "preserveJsonSchemas": ["AmbiguousPayload", "Payloads", "ComposedUnion"]}
    config_path = work / "hawaii.json"
    config_path.write_text(json.dumps(config, indent=2) + "\n")
    run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], ROOT, work / "generate.log")
    fidelity = json.loads((generated / "schema-fidelity.json").read_text())
    if {item["sourceSchema"] for item in fidelity["preservedSchemas"]} != {"AmbiguousPayload", "Payloads", "ComposedUnion"}:
        raise RuntimeError("Fidelity report does not match the explicit preservation policy")
    if fidelity["configSha256"] != hashlib.sha256(config_path.read_bytes()).hexdigest() or fidelity["sourceSha256"] != hashlib.sha256((HERE / "status-openapi.json").read_bytes()).hexdigest():
        raise RuntimeError("Fidelity report fingerprints do not identify the actual inputs")
    intersection_report = next(item for item in fidelity["preservedSchemas"] if item["sourceSchema"] == "ComposedUnion")
    if intersection_report["composition"] != "allOf-with-union" or "allOf" not in intersection_report["constraints"]:
        raise RuntimeError("Composed preservation report omitted intersection constraints")
    runtime = work / "Runtime"
    runtime.mkdir(exist_ok=True)
    (runtime / "Runtime.fsproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>'
        '<ProjectReference Include="../Generated/Hawaii.StatusRegression.fsproj" />'
        f'<Compile Include="{escape(str(HERE / "StatusRuntime.fs"), {chr(34): "&quot;"})}" />'
        '</ItemGroup></Project>\n')
    for configuration in ["Debug", "Release"]:
        output = run(["dotnet", "run", "--configuration", configuration, "--project", str(runtime / "Runtime.fsproj")],
                     ROOT, work / f"runtime-{configuration.lower()}.log")
        print(f"{configuration}: {output.strip()}")
    # Parser failures must fail generation even when OpenAPI.NET supplies a partial document.
    invalid = json.loads((HERE / "status-openapi.json").read_text())
    invalid["paths"]["/dispatch"]["get"]["responses"]["200"]["content"]["application/json"]["schema"] = {"$ref": "#/components/schemas/Missing"}
    invalid_path = work / "invalid-reference.json"
    invalid_path.write_text(json.dumps(invalid))
    config["schema"] = str(invalid_path)
    config["output"] = str(work / "InvalidGenerated")
    config_path.write_text(json.dumps(config))
    result = subprocess.run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], cwd=ROOT,
                            text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (work / "invalid-reference.log").write_text(result.stdout)
    if result.returncode == 0 or (work / "InvalidGenerated").exists():
        raise RuntimeError("An unresolved schema reference did not reject generation before output")
    print("Invalid reference rejected.")
    for label, additions, diagnostic in [
        ("unsupported-union", {"Ambiguous": {"oneOf": [{"type": "object"}, {"type": "object", "properties": {"x": {"type": "string"}}}]}}, "Unsupported named schema declarations"),
        ("name-collision", {"a_u002D_b": {"type": "integer"}}, "collide at generated type"),
    ]:
        fixture = json.loads((HERE / "status-openapi.json").read_text())
        fixture["components"]["schemas"].update(additions)
        fixture_path = work / f"{label}.json"
        fixture_path.write_text(json.dumps(fixture))
        config["schema"] = str(fixture_path)
        config["output"] = str(work / f"Rejected-{label}")
        config_path.write_text(json.dumps(config))
        result = subprocess.run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], cwd=ROOT,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (work / f"{label}.log").write_text(result.stdout)
        if result.returncode != 1 or diagnostic not in result.stdout or list(Path(config["output"]).glob("*.fs")):
            raise RuntimeError(f"{label} did not produce a clean diagnostic before source output: {result.stdout}")
        print(f"{label} rejected with a diagnostic.")
    for label, policy, target, diagnostic in [
        ("unknown-policy-name", ["Missing"], "fsharp", "Unknown preserveJsonSchemas entry"),
        ("duplicate-policy-name", ["AmbiguousPayload", "AmbiguousPayload"], "fsharp", "duplicate schema names"),
        ("nonunion-policy-name", ["TagA"], "fsharp", "not a union, composed union, or array of unions"),
        ("unsupported-policy-target", ["AmbiguousPayload"], "fable", "requires the fsharp target"),
        ("default-policy", [], "fsharp", "explicit preserveJsonSchemas entry"),
    ]:
        config.update(schema=str(HERE / "status-openapi.json"), output=str(work / f"Rejected-{label}"),
                      preserveJsonSchemas=policy, target=target)
        config_path.write_text(json.dumps(config))
        result = subprocess.run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], cwd=ROOT,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (work / f"{label}.log").write_text(result.stdout)
        if result.returncode != 1 or diagnostic not in result.stdout or list(Path(config["output"]).glob("*.fs")):
            raise RuntimeError(f"{label} was not rejected before generated source: {result.stdout}")
        print(f"{label} rejected with a diagnostic.")
    for label, mutate, diagnostic in [
        ("malformed-query-encoding", lambda fixture: fixture["paths"]["/parameters/{id}"]["get"]["parameters"][0].update({"x-hawaii-query-encoding": {"unexpected": True}}), "Query encoding must be a string"),
        ("unsupported-request-media", lambda fixture: fixture["paths"]["/sql"]["post"]["requestBody"].update({"content": {"application/xml": {"schema": {"type": "object"}}}}), "Unsupported request body encoding"),
    ]:
        fixture = json.loads((HERE / "status-openapi.json").read_text())
        mutate(fixture)
        fixture_path = work / f"{label}.json"
        fixture_path.write_text(json.dumps(fixture))
        config.update(schema=str(fixture_path), output=str(work / f"Rejected-{label}"),
                      preserveJsonSchemas=["AmbiguousPayload", "Payloads", "ComposedUnion"], target="fsharp")
        config_path.write_text(json.dumps(config))
        result = subprocess.run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], cwd=ROOT,
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (work / f"{label}.log").write_text(result.stdout)
        if result.returncode != 1 or diagnostic not in result.stdout or list(Path(config["output"]).glob("*.fs")):
            raise RuntimeError(f"{label} was not rejected before generated source: {result.stdout}")
        print(f"{label} rejected with a diagnostic.")
    print(f"Logs: {work}")


if __name__ == "__main__":
    main()
