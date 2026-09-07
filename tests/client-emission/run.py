#!/usr/bin/env python3
"""Compile generated task/async clients and exercise them through a fake HTTP handler."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def run(command, log):
    result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.write_text(result.stdout)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}\n{result.stdout}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, help="Use an already-built generator")
    parser.add_argument("--work-directory", type=Path)
    args = parser.parse_args()
    work = (args.work_directory or Path(tempfile.mkdtemp(prefix="hawaii-client-emission-"))).resolve()
    if work.exists() and any(work.iterdir()):
        raise ValueError(f"Work directory is not empty: {work}")
    work.mkdir(parents=True, exist_ok=True)
    if args.assembly:
        assembly = args.assembly.resolve()
    else:
        run(["dotnet", "build", "src/Hawaii.fsproj", "--nologo"], work / "tool-build.log")
        assembly = ROOT / "src/bin/Debug/net10.0/Hawaii.dll"
    for mode in ["task", "async"]:
        case = work / mode
        case.mkdir()
        generated = case / "Generated"
        config = {"schema": str(HERE / "client-openapi.json"), "project": "Hawaii.ClientEmission",
                  "output": str(generated), "target": "fsharp", "asyncReturnType": mode}
        config_path = case / "hawaii.json"
        config_path.write_text(json.dumps(config, indent=2) + "\n")
        run(["dotnet", str(assembly), "--config", str(config_path), "--no-logo"], case / "generate.log")
        runtime = case / "Runtime"
        runtime.mkdir()
        (runtime / "Runtime.fsproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            f'<TargetFramework>net10.0</TargetFramework><DefineConstants>{mode.upper()}</DefineConstants>'
            '</PropertyGroup><ItemGroup><ProjectReference Include="../Generated/Hawaii.ClientEmission.fsproj" />'
            f'<Compile Include="{escape(str(HERE / "Runtime.fs"), {chr(34): "&quot;"})}" />'
            '</ItemGroup></Project>\n')
        for configuration in ["Debug", "Release"]:
            output = run(["dotnet", "run", "--configuration", configuration, "--project", str(runtime / "Runtime.fsproj")],
                         case / f"runtime-{configuration.lower()}.log")
            print(f"{mode}/{configuration}: {output.strip()}")
    print(f"Logs: {work}")


if __name__ == "__main__":
    main()
