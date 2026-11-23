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
SetupIconFile=..\src\PrintInterceptor\Resources\app-icon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no
; build-release.ps1 defines SignToolConfigured and supplies the /Ssigntool
; definition only when it has a signing certificate. Without this directive
; Inno Setup would leave the setup and uninstaller unsigned.
#ifdef SignToolConfigured
SignTool=signtool
#endif

[Tasks]
Name: "printer"; Description: "Install the FiscalStack virtual printer (TCP 127.0.0.1:{#RunPort})"; GroupDescription: "POS integration:"; Flags: checkedonce
#ifdef GhostscriptBundled
; No Flags: Inno tasks are checked by default, and checkedonce would silently
; drop Ghostscript on an upgrade because the previous version is already present.
Name: "ghostscript"; Description: "Install bundled 64-bit Ghostscript for PDF/PostScript capture and original-invoice stamping (AGPL-3.0)"; GroupDescription: "Optional components:"
#endif
Name: "traymode"; Description: "Start as a tray app at sign-in instead of a Windows service"; GroupDescription: "Run mode:"

[Files]
; The Ghostscript bundle is a separate task so an operator can see and decline
; its AGPL-3.0 obligations; the rest of the application is always installed.
; Excludes matches file paths, not directories: \ghostscript\* is required to
; keep every bundled file out of the always-installed entry.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "\ghostscript\*"
#ifdef GhostscriptBundled
Source: "..\publish\ghostscript\*"; DestDir: "{app}\ghostscript"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: ghostscript
#endif

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
  // Pascal single-quoted strings do not double embedded quotes: '""' would put
  // two literal quotes into CreateProcess's command line, and PowerShell would
  // then read only "C:\Program" as -ExePath. '"' is one quote character.
  Args := '-ExePath "' + ExpandConstant('{app}\{#AppExeName}') + '" -Port {#RunPort}';

  if WizardIsTaskSelected('traymode') then
    Args := Args + ' -RunMode Tray'
  else
    Args := Args + ' -RunMode Service';

  if not WizardIsTaskSelected('printer') then
    Args := Args + ' -SkipPrinter';

#ifdef GhostscriptBundled
  if WizardIsTaskSelected('ghostscript') then
  begin
    Args := Args + ' -GhostscriptPath "' + ExpandConstant('{app}\ghostscript\bin\gswin64c.exe') + '"';
    // The PS class driver sends Palladium's A4 invoice as PostScript. Text-mode
    // capture fragments those glyphs into unparseable lines; PostScript goes through
    // Ghostscript txtwrite and is also archived as a PDF.
    Args := Args + ' -PrinterMode PostScript';
  end;
#endif

  Result := Args;
end;

// install.ps1 runs hidden from [Run], so Setup never sees its exit code and a till
// left without a printer or without a service still looks like a clean install. The
// script writes its result to a status file instead, and this reads it back once
// everything has run: ssDone is the last step, after all [Run] entries.
procedure CurStepChanged(CurStep: TSetupStep);
var
  StatusPath, State, Failures, Line, Message, Repair: String;
  StatusLines: TArrayOfString;
  I: Integer;
begin
  if CurStep <> ssDone then Exit;

  StatusPath := ExpandConstant('{commonappdata}\FiscalStack\PrintInterceptor\install-status.txt');
  State := 'missing';
  Failures := '';

  if LoadStringsFromFile(StatusPath, StatusLines) then
  begin
    for I := 0 to GetArrayLength(StatusLines) - 1 do
    begin
      Line := TrimRight(StatusLines[I]);
      if Copy(Line, 1, 6) = 'state=' then
        State := Copy(Line, 7, Length(Line) - 6);
      if Copy(Line, 1, 8) = 'failure=' then
      begin
        if Failures <> '' then Failures := Failures + #13#10;
        Failures := Failures + Copy(Line, 9, Length(Line) - 8);
      end;
    end;
  end;

  if State = 'ok' then Exit;

  if Failures = '' then
    Failures := 'install.ps1 left no status at ' + StatusPath + ', so the printer, the'
      + #13#10 + 'configuration and the run mode may all still be unset.';

  Repair := '  & "' + ExpandConstant('{app}\addprinter.ps1') + '" -PrinterName "{#PrinterName}"'
    + ' -Port {#RunPort} -Mode Text';

  Message := 'PrintInterceptor was copied to this machine, but the configuration step did not' + #13#10
    + 'finish (status: ' + State + '). Without the "{#PrinterName}" printer no print job' + #13#10
    + 'reaches 127.0.0.1:{#RunPort}, so nothing is fiscalised.' + #13#10#13#10 + Failures
    + #13#10#13#10 + 'Repair from an elevated PowerShell:' + #13#10 + Repair
    + #13#10#13#10 + 'Then re-run this installer, or start the app manually.';

  Log('Install did not complete: ' + State + '; ' + Failures);
  if not WizardSilent then
    MsgBox(Message, mbError, MB_OK);
end;
