#ifndef PublishDir
  #error PublishDir must point to the self-contained publish directory.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1.
#endif

[Setup]
AppId={{EBC38F35-3311-4F6C-9014-3AF4E43E3D51}
AppName=WinLanChat
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\WinLanChat
DefaultGroupName=WinLanChat
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=WinLanChat-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\WinLanChat.App\Assets\WinLanChat.ico
UninstallDisplayIcon={app}\WinLanChat.App.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
AppMutex=Global\WinLanChat.SingleInstance
SetupMutex=WinLanChat.Setup
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinLanChat"; Filename: "{app}\WinLanChat.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\WinLanChat"; Filename: "{app}\WinLanChat.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WinLanChat.App.exe"; Description: "{cm:LaunchProgram,WinLanChat}"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsUpdate
Filename: "{app}\WinLanChat.App.exe"; Flags: nowait; Check: IsUpdate

[CustomMessages]
english.UpdateWaitFailed=WinLanChat has not finished closing. Exit it from the system tray and run the installer again.
russian.UpdateWaitFailed=WinLanChat ещё не завершил работу. Выйдите из приложения через значок в трее и запустите установщик повторно.

[Code]
function OpenProcess(DesiredAccess: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function InitializeSetup: Boolean;
var
  ProcessId: Integer;
  ProcessHandle: THandle;
begin
  Result := True;
  if not IsUpdate then Exit;
  ProcessId := StrToIntDef(ExpandConstant('{param:WAITFORPID|0}'), 0);
  if ProcessId <= 0 then
    Result := False
  else begin
    ProcessHandle := OpenProcess($00100000, False, ProcessId);
    if ProcessHandle <> 0 then begin
      try
        Result := WaitForSingleObject(ProcessHandle, 30000) = 0;
      finally
        CloseHandle(ProcessHandle);
      end;
    end else
      { ERROR_INVALID_PARAMETER means the parent process has already exited. }
      Result := DLLGetLastError = 87;
  end;
  if not Result then
    MsgBox(CustomMessage('UpdateWaitFailed'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'WinLanChat', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\WinLanChat.App.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WinLanChat');
end;
