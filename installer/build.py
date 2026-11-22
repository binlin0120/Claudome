"""Developer build only. Customer PCs need neither Go nor the .NET SDK."""
import argparse
import json
import logging
import os
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.request
import hashlib
import zipfile

BASE = Path(__file__).resolve().parent
ARTIFACTS = BASE / "artifacts"


def run(args, cwd):
    logging.info("Running %s", Path(str(args[0])).name)
    subprocess.run([str(x) for x in args], cwd=cwd, check=True)


def notices(go):
    # The module graph also contains unused tooling dependencies without a Dir.
    # Attribute modules actually compiled into this helper instead.
    raw = subprocess.check_output([go, "list", "-deps", "-f", "{{with .Module}}{{.Path}}|{{.Version}}|{{.Dir}}{{end}}", "."], cwd=BASE / "network-gateway", text=True, encoding="utf-8")
    modules = []
    for line in sorted(set(raw.splitlines())):
        if not line.strip():
            continue
        name, version, directory = line.split("|", 2)
        if version:
            modules.append({"Path": name, "Version": version, "Dir": directory})
    sections = ["Claudome - bundled gateway dependency notices\n\n"
                "Project code is MIT. Third-party licenses below retain their own terms.\n"
                "Cage was studied as a design reference; its code is not included.\n"]
    goroot = Path(subprocess.check_output([go, "env", "GOROOT"], text=True).strip())
    sections.append("Go runtime\n" + (goroot / "LICENSE").read_text(encoding="utf-8"))
    inventory = []
    for module in modules:
        if module.get("Main"):
            continue
        directory = Path(module["Dir"])
        licenses = sorted({p for p in directory.iterdir() if p.is_file() and p.name.upper().split(".")[0] in {"LICENSE", "LICENCE", "COPYING", "NOTICE", "COPYRIGHT"}})
        if not licenses:
            raise RuntimeError("Missing license: " + module["Path"])
        sections.append("\n\n=== " + module["Path"] + " " + module["Version"] + " ===\n")
        for file in licenses:
            sections.append("\n" + file.name + "\n" + file.read_text(encoding="utf-8"))
        inventory.append({"module": module["Path"], "version": module["Version"], "licenses": [p.name for p in licenses]})
    (ARTIFACTS / "Gateway-THIRD-PARTY-NOTICES.txt").write_text("\n".join(sections), encoding="utf-8")
    (ARTIFACTS / "gateway-dependencies.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")


def guest_network(dotnet):
    destination = ARTIFACTS / "wintun"
    destination.mkdir(exist_ok=True)
    archive = ARTIFACTS / "wintun-0.14.1.zip"
    if not archive.exists():
        with urllib.request.urlopen("https://www.wintun.net/builds/wintun-0.14.1.zip", timeout=60) as response:
            archive.write_bytes(response.read())
    if hashlib.sha256(archive.read_bytes()).hexdigest() != "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51":
        raise RuntimeError("Official Wintun archive SHA-256 mismatch")
    with zipfile.ZipFile(archive) as content:
        (destination / "wintun.dll").write_bytes(content.read("wintun/bin/amd64/wintun.dll"))
        (destination / "LICENSE.txt").write_bytes(content.read("wintun/LICENSE.txt"))
    run([dotnet, "publish", BASE / "GuestNetwork" / "GuestNetwork.csproj", "-c", "Release", "--nologo", "-o", ARTIFACTS / "guest-network"], BASE)
    shutil.copy2(ARTIFACTS / "guest-network" / "ClaudomeGuestNetwork.exe", ARTIFACTS / "ClaudomeGuestNetwork.exe")
    with (ARTIFACTS / "Gateway-THIRD-PARTY-NOTICES.txt").open("a", encoding="utf-8") as output:
        output.write("\n\n=== Official unmodified Wintun 0.14.1 signed binary ===\n")
        output.write((destination / "LICENSE.txt").read_text(encoding="utf-8"))


def desktop_notices(project):
    assets = json.loads((project.parent / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    roots = [Path(p) for p in assets["packageFolders"]]
    dependencies = {}
    for framework in assets["project"]["frameworks"].values():
        for item in framework.get("downloadDependencies", []):
            if item["name"].lower() in {"microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64"}:
                version = item["version"].strip("[]").split(",")[0].strip()
                dependencies[item["name"]] = (item["name"].lower() + "/" + version, version)
    if len(dependencies) != 2:
        raise RuntimeError("Cannot identify both actual .NET runtime packs for license collection")
    notices_path = ARTIFACTS / "Gateway-THIRD-PARTY-NOTICES.txt"
    with notices_path.open("a", encoding="utf-8") as output:
        for name, (relative, version) in dependencies.items():
            directory = next((p / relative for p in roots if (p / relative).is_dir()), None)
            if directory is None:
                raise RuntimeError("Missing runtime license directory: " + name)
            files = [p for p in directory.iterdir() if p.is_file() and (p.name.upper().split(".")[0] == "LICENSE" or "NOTICES" in p.name.upper())]
            if not files:
                raise RuntimeError("Missing runtime license: " + name)
            output.write("\n\n=== " + name + " " + version + " ===\n")
            for file in files:
                output.write(file.read_text(encoding="utf-8-sig"))
        license_file = ARTIFACTS / "YamlDotNet-18.1.0-LICENSE.txt"
        if not license_file.exists():
            with urllib.request.urlopen("https://raw.githubusercontent.com/aaubry/YamlDotNet/v18.1.0/LICENSE.txt", timeout=30) as response:
                data = response.read(32769)
            if len(data) > 32768 or b"Antoine Aubry" not in data or b"Permission is hereby granted" not in data:
                raise RuntimeError("YamlDotNet upstream license could not be verified")
            license_file.write_bytes(data)
        output.write("\n\n=== YamlDotNet 18.1.0 ===\n" + license_file.read_text(encoding="utf-8"))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--go", default=shutil.which("go"))
    parser.add_argument("--test", action="store_true")
    parser.add_argument("--ui-test", action="store_true", help="Render WPF and run synthetic UI scenarios without VM actions")
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    logging.basicConfig(level=logging.INFO, handlers=[logging.FileHandler(ARTIFACTS / "build.log", encoding="utf-8"), logging.StreamHandler()])
    try:
        if os.name != "nt" or not args.go or not args.dotnet:
            raise RuntimeError("Windows developer build needs Go and .NET 10 SDK. Specify --go / --dotnet if they are not on PATH.")
        gateway = BASE / "network-gateway"
        if args.test:
            run([args.go, "test", "./...", "-count=1"], gateway)
        run([args.go, "build", "-trimpath", "-buildvcs=false", "-ldflags=-s -w", "-o", ARTIFACTS / "ClaudeNetworkGateway.exe", "."], gateway)
        notices(args.go)
        guest_network(args.dotnet)
        project = BASE / "ClaudeSetup" / "ClaudeSetup.csproj"
        run([args.dotnet, "restore", project, "--nologo"], BASE)
        desktop_notices(project)
        run([args.dotnet, "build", project, "-c", "Release", "--nologo"], BASE)
        executable = BASE / "ClaudeSetup" / "bin" / "Release" / "net10.0-windows" / "win-x64" / "Claudome.exe"
        if args.test:
            run([executable, "--self-test", ARTIFACTS / "tests"], BASE)
            pwsh = shutil.which("pwsh")
            if not pwsh:
                raise RuntimeError("Developer offline PowerShell fixtures require PowerShell 7")
            for name in ("HyperVPolicy", "InteractivePlan", "GuestSource", "ReleaseReview", "HyperVRecovery", "Alignment", "Experience"):
                run([pwsh, "-NoLogo", "-NoProfile", "-File", BASE / "tests" / (name + ".Tests.ps1"), "-OutputDirectory", ARTIFACTS / "tests"], BASE)
            run([sys.executable, BASE / "tests" / "VBoxCliContract.py", "--output-directory", ARTIFACTS / "tests"], BASE)
        if args.ui_test:
            run([executable, "--preview", ARTIFACTS / "ui-tests"], BASE)
            report = json.loads((ARTIFACTS / "ui-tests" / "customer-feedback-ui-tests.json").read_text(encoding="utf-8"))
            if report["failed"]:
                raise RuntimeError("WPF UI regression tests failed")
        if args.publish:
            run([args.dotnet, "publish", project, "-c", "Release", "--no-restore", "--nologo", "-o", ARTIFACTS / "Claudome-publish"], BASE)
            shutil.copy2(ARTIFACTS / "Gateway-THIRD-PARTY-NOTICES.txt", ARTIFACTS / "Claudome-publish")
            shutil.copy2(BASE.parent / "LICENSE", ARTIFACTS / "Claudome-publish" / "PROJECT-LICENSE.txt")
        logging.info("Build complete. Live boundary checks are required before VM start; physical VM acceptance has not been run.")
        return 0
    except Exception:
        logging.exception("Build failed")
        return 1


if __name__ == "__main__":
    sys.exit(main())
