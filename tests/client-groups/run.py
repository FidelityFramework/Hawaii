#!/usr/bin/env python3
"""Verify grouped clients retain model identity, operation coverage, and a project DAG."""
import argparse
import copy
import json
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def run(command, log, expected=0):
    result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.write_text(result.stdout)
    if result.returncode != expected:
        raise RuntimeError(f"Unexpected exit {result.returncode}; see {log}\n{result.stdout}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path)
    parser.add_argument("--work-directory", type=Path)
    args = parser.parse_args()
    work = (args.work_directory or Path(tempfile.mkdtemp(prefix="hawaii-client-groups-"))).resolve()
    if work.exists() and any(work.iterdir()):
        raise ValueError(f"Work directory is not empty: {work}")
    work.mkdir(parents=True, exist_ok=True)
    if args.assembly:
        assembly = args.assembly.resolve()
    else:
        run(["dotnet", "build", "src/Hawaii.fsproj", "--nologo"], work / "tool-build.log")
        assembly = ROOT / "src/bin/Debug/net10.0/Hawaii.dll"
    base = {"schema": str(HERE / "grouped-openapi.json"), "project": "GroupFixture.Core", "target": "fsharp",
            "clientGroups": [{"project": "GroupFixture.First", "operationIds": ["SendShared"]},
                             {"project": "GroupFixture.Second", "operationIds": ["FetchShared", "Legacy"]}]}
    for mode in ["task", "async"]:
        case = work / mode
        case.mkdir()
        generated = case / "Generated"
        config = base | {"output": str(generated), "asyncReturnType": mode}
        config_path = case / "hawaii.json"
        config_path.write_text(json.dumps(config, indent=2) + "\n")
        command = ["dotnet", str(assembly), "--config", str(config_path), "--no-logo"]
        run(command, case / "generate.log")
        manifest = json.loads((generated / "client-groups.json").read_text())
        assert manifest["sharedProject"] == "GroupFixture.Core"
        assert sorted(op for client in manifest["clients"] for op in client["operationIds"]) == ["FetchShared", "Legacy", "SendShared"]
        assert [str(path.relative_to(generated)) for path in generated.rglob("Types.fs")] == ["Types.fs"]
        assert not (generated / "Client.fs").exists()
        deterministic = {str(path.relative_to(generated)): path.read_bytes() for path in generated.rglob("*")
                         if path.suffix in {".fs", ".fsproj"} or path.name == "client-groups.json"}
        reordered = copy.deepcopy(config)
        reordered["clientGroups"].reverse()
        for group in reordered["clientGroups"]:
            group["operationIds"].reverse()
        config_path.write_text(json.dumps(reordered, indent=2) + "\n")
        run(command, case / "reordered-generate.log")
        assert all((generated / name).read_bytes() == data for name, data in deterministic.items())
        runtime = case / "Runtime"
        runtime.mkdir()
        references = ''.join(f'<ProjectReference Include="../Generated/{client["projectFile"]}" />' for client in manifest["clients"])
        (runtime / "Runtime.fsproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            f'<TargetFramework>net10.0</TargetFramework><DefineConstants>{mode.upper()}</DefineConstants>'
            f'</PropertyGroup><ItemGroup>{references}'
            f'<Compile Include="{escape(str(HERE / "Runtime.fs"), {chr(34): "&quot;"})}" />'
            '</ItemGroup></Project>\n')
        for configuration in ["Debug", "Release"]:
            output = run(["dotnet", "run", "--configuration", configuration, "--project", str(runtime / "Runtime.fsproj")],
                         case / f"runtime-{configuration.lower()}.log")
            print(f"{mode}/{configuration}: {output.strip()}")

    invalid = [
        ({"clientGroups": [{"project": "GroupFixture.First", "operationIds": ["SendShared"]}]}, "omit selected"),
        ({"clientGroups": base["clientGroups"] + [{"project": "GroupFixture.Third", "operationIds": ["SendShared"]}]}, "more than one"),
        ({"clientGroups": base["clientGroups"] + [{"project": "groupfixture.first", "operationIds": ["missing"]}]}, "Duplicate client group"),
        ({"clientGroups": [{"project": "GroupFixture.Core", "operationIds": ["SendShared"]}]}, "shared model"),
        ({"clientGroups": [{"project": "GroupFixture.First", "operationIds": ["missing"]}]}, "unknown or filtered"),
        ({"clientGroups": [{"project": "../Escape", "operationIds": ["SendShared"]}]}, "dotted identifier"),
        ({"clientGroups": [{"project": "GroupFixture.First", "operationIds": []}]}, "nonempty operationIds"),
        ({"clientGroups": [{"project": "GroupFixture.First", "operationIds": [1]}]}, "nonempty operationIds"),
        ({"clientGroups": "typo"}, "must be an array"),
        ({"target": "fable"}, "fsharp target only"),
        ({"filterTags": ["unselected"]}, "unknown or filtered"),
    ]
    for index, (change, expected) in enumerate(invalid):
        case = work / f"invalid-{index}"
        case.mkdir()
        generated = case / "Generated"
        generated.mkdir()
        sentinel = generated / "existing.fs"
        sentinel.write_text("previous validated output\n")
        config = base | {"output": str(generated)} | change
        config_path = case / "hawaii.json"
        config_path.write_text(json.dumps(config, indent=2) + "\n")
        output = run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], case / "generate.log", expected=1)
        assert expected in output, output
        assert sentinel.read_text() == "previous validated output\n"
    print(f"{len(invalid)} invalid partitions rejected before modifying output. Logs: {work}")


if __name__ == "__main__":
    main()
