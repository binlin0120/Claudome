"""Read-only compatibility check against the installed VirtualBox CLI usage contract."""
from pathlib import Path
import argparse
import json
import logging
import os
import re
import subprocess
import winreg


def installed_vbox():
    candidates=[]
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SOFTWARE\Oracle\VirtualBox',0,winreg.KEY_READ|winreg.KEY_WOW64_64KEY) as key:
            candidates.append(Path(winreg.QueryValueEx(key,'InstallDir')[0])/'VBoxManage.exe')
    except FileNotFoundError:
        pass
    candidates.append(Path(os.environ['ProgramFiles'])/'Oracle/VirtualBox/VBoxManage.exe')
    return next((path for path in candidates if path.is_file()),None)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output-directory',type=Path,required=True)
    args=parser.parse_args()
    args.output_directory.mkdir(parents=True,exist_ok=True)
    logging.basicConfig(level=logging.INFO,handlers=[logging.FileHandler(args.output_directory/'vbox-cli-contract.log',encoding='utf-8'),logging.StreamHandler()])
    report={'type':'installed CLI help and source arguments only; no VM created, read or modified','passed':0,'failed':0,'skipped':0,'tests':[]}
    try:
        executable=installed_vbox()
        if executable is None:
            report['skipped']=1
            report['tests'].append({'name':'installed-VirtualBox-recovery-argument-contract','skipped':True,'reason':'VirtualBox not installed on this developer machine; actual CLI validation remains pending'})
        else:
            process=subprocess.run([str(executable),'modifyvm','--help'],capture_output=True,timeout=30,creationflags=subprocess.CREATE_NO_WINDOW)
            help_text=(process.stdout+process.stderr).decode('utf-8',errors='replace')
            clipboard=re.search(r'--clipboard-file-transfers=\s*(enabled|disabled)\s*\|\s*(enabled|disabled)',help_text)
            if process.returncode not in (0,2) or clipboard is None:
                raise ValueError('Installed CLI did not provide the expected modifyvm argument contract')
            source=(Path(__file__).resolve().parents[1]/'ClaudeSetup/VirtualBoxLifecycle.cs').read_text(encoding='utf-8-sig')
            call=re.search(r'await Run\(token,"modifyvm",state\.VmId,"--clipboard-mode",(.*)\);',source)
            if call is None:raise ValueError('Production sharing recovery call missing')
            literals=['--clipboard-mode']+re.findall(r'"([^"\r\n]*)"',call[1])
            if len(literals)%2:raise ValueError('Production recovery arguments are not pairs')
            values=dict(zip(literals[::2],literals[1::2]))
            contracts={'--clipboard-mode':{'disabled','hosttoguest','guesttohost','bidirectional'},'--clipboard-file-transfers':set(clipboard.groups()),'--drag-and-drop':{'disabled','hosttoguest','guesttohost','bidirectional'},'--usb-ohci':{'on','off'},'--usb-ehci':{'on','off'},'--usb-xhci':{'on','off'},'--mouse':{'ps2','usb','usbtablet','usbmultitouch','usbmtscreenpluspad'},'--vrde':{'on','off'}}
            invalid=[name for name,value in values.items() if name not in contracts or value not in contracts[name]]
            if invalid:raise ValueError('Unsupported production recovery options: '+', '.join(invalid))
            for option in contracts:
                if option not in values:raise ValueError('Required isolation option missing: '+option)
                if option not in help_text:raise ValueError('Installed CLI does not expose required option: '+option)
            nem_source=(Path(__file__).resolve().parents[1]/'ClaudeSetup/VirtualBoxNemCompatibility.cs').read_text(encoding='utf-8-sig')
            if '"--x86-x2apic", "off"' in nem_source:
                raise ValueError('Do not pass --x86-x2apic off in VBox 7.2; it disables the entire 64-bit APIC enum')
            for required_flag in ('--cpus','--apic','--x86-x2apic','--ioapic','--paravirt-provider','default','--x86-hpet'):
                if required_flag not in nem_source or required_flag not in help_text:
                    raise ValueError('NEM compatibility flag missing from source or installed VBoxManage: '+required_flag)
            storage_proc=subprocess.run([str(executable),'storageattach','--help'],capture_output=True,timeout=30,creationflags=subprocess.CREATE_NO_WINDOW)
            storage_help=(storage_proc.stdout+storage_proc.stderr).decode('utf-8',errors='replace')
            for storage_flag in ('--forceunmount','emptydrive','dvddrive','fdd'):
                if storage_flag not in storage_help:
                    raise ValueError('Installed storageattach CLI missing required flag: '+storage_flag)
            report['passed']=1
            report['tests'].append({'name':'installed-VirtualBox-recovery-argument-contract','passed':True,'helpExitCode':process.returncode,'recoveryArguments':values})
    except Exception:
        logging.exception('Read-only CLI contract check failed')
        report['failed']=1
        report['tests'].append({'name':'installed-VirtualBox-recovery-argument-contract','passed':False})
    destination=args.output_directory/'vbox-cli-contract-tests.json'
    destination.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    logging.info('CLI contract: passed=%d failed=%d skipped=%d',report['passed'],report['failed'],report['skipped'])
    return 1 if report['failed'] else 0


if __name__=='__main__':raise SystemExit(main())
