; Inno Setup 6 script for the PrintInterceptor setup .exe.
; Build with packaging\build-release.ps1 (stages the publish folder and the
; install helpers first). Compiling and running this requires Windows.

#define AppName "FiscalStack PrintInterceptor"
#define AppPublisher "FiscalStack"
#define AppExeName "PrintInterceptor.exe"
#define PrinterName "FiscalStack Fiscal Printer"
#define ServiceName "PrintInterceptor"
#define RunPort "9100"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{0F5D3A47-8C1E-4E49-9C54-4A4D8F3E6B21}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FiscalStack\PrintInterceptor
DefaultGroupName=FiscalStack
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=PrintInterceptor-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "printer"; Description: "Install the FiscalStack virtual printer (TCP 127.0.0.1:{#RunPort})"; GroupDescription: "POS integration:"; Flags: checkedonce
Name: "traymode"; Description: "Start as a tray app at sign-in instead of a Windows service"; GroupDescription: "Run mode:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PrintInterceptor tray"; Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; Tasks: traymode
Name: "{group}\Uninstall PrintInterceptor"; Filename: "{uninstallexe}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" {code:GetInstallArguments}"; StatusMsg: "Installing the virtual printer and configuring the till..."; Flags: runhidden waituntilterminated
; Always open the first-run setup dialog after installation. It does not start a
; second print listener, so a service install can still show the Settings form
; while the Windows service handles capture in the background.
Filename: "{app}\{#AppExeName}"; Parameters: "--configure-only --setup"; Description: "Open PrintInterceptor settings to complete the first-run setup"; Flags: nowait postinstall skipifsilent
; Only start the resident tray app when tray mode was selected; otherwise the
; Windows service owns the pipeline and the tray would double-bind 127.0.0.1.
Filename: "{app}\{#AppExeName}"; Parameters: "--tray"; Description: "Start the PrintInterceptor tray icon now"; Flags: nowait postinstall skipifsilent; Tasks: traymode

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -Uninstall -ServiceName ""{#ServiceName}"" -PrinterName ""{#PrinterName}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemovePrintInterceptor"

[Code]
function GetInstallArguments(Param: String): String;
var
  Args: String;
begin
  Args := '-ExePath ""' + ExpandConstant('{app}\{#AppExeName}') + '"" -Port {#RunPort}';

  if WizardIsTaskSelected('traymode') then
    Args := Args + ' -RunMode Tray'
  else
    Args := Args + ' -RunMode Service';

  if not WizardIsTaskSelected('printer') then
    Args := Args + ' -SkipPrinter';

  Result := Args;
end;
