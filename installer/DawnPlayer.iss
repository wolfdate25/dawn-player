; Dawn Player - Inno Setup 6 Script
; Copyright (c) Dawn Player Project. All rights reserved.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyAppName
  #define MyAppName "Dawn Player"
#endif

#ifndef MyAppPublisher
  #define MyAppPublisher "Dawn Player Project"
#endif

#ifndef MyAppURL
  #define MyAppURL "https://github.com/dawn-player"
#endif

#ifndef MyAppExeName
  #define MyAppExeName "DawnPlayer.App.exe"
#endif

#ifndef MySourceDir
  #define MySourceDir "..\dist\publish"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\dist\installer"
#endif

[Setup]
; App Identity
AppId={{8E37E3E5-A98B-4B3B-8C47-5D4C3A3B4401}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Architecture & Platform
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Target Directory & Group
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=DawnPlayer-Setup-v{#MyAppVersion}-x64
SetupIconFile=compiler:SetupClassicIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE

; Compression
Compression=lzma2/ultra64
SolidCompression=yes

; Permissions & Privilege Model (Per-user default, allows elevation to per-machine)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsedUserAreasWarning=no

; Process & Restart Management
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=yes

; UI Configuration
WizardStyle=modern
DisableProgramGroupPage=yes
DisableWelcomePage=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; Korean
korean.AdditionalIconsGroup=바로가기 생성:
korean.OtherGroup=추가 설정:
korean.DesktopIconTask=바탕 화면에 바로가기 만들기(&D)
korean.AutoStartTask=Windows 시작 시 자동 실행(&S)
korean.FileAssocTask=기본 오디오 파일 연결 (.mp3, .flac, .wav, .m4a, .ogg 등)(&A)
korean.PlaylistAssocTask=재생목록 파일 연결 (.m3u, .m3u8, .cue, .lrc)(&P)
korean.ContextMenuPlay=Dawn Player로 재생
korean.ContextMenuEnqueue=Dawn Player 대기열에 추가
korean.AppRunningPrompt=Dawn Player가 현재 실행 중입니다.%n설치를 계속하려면 실행 중인 Dawn Player를 종료해야 합니다.%n%n앱을 자동으로 종료하고 계속하시겠습니까?
korean.UninstallAppRunningPrompt=Dawn Player가 현재 실행 중입니다.%n제거를 계속하려면 실행 중인 Dawn Player를 종료해야 합니다.%n%n앱을 자동으로 종료하고 계속하시겠습니까?
korean.CleanAppDataPrompt=Dawn Player의 사용자 데이터(설정, 라이브러리 DB, 재생목록, 앨범아트 캐시)를 완전히 삭제하시겠습니까?%n(선택하지 않으면 향후 재설치 시 설정이 유지됩니다)

; English
english.AdditionalIconsGroup=Shortcuts:
english.OtherGroup=Additional settings:
english.DesktopIconTask=Create a &desktop shortcut
english.AutoStartTask=Start Dawn Player with &Windows
english.FileAssocTask=Associate with &audio files (.mp3, .flac, .wav, .m4a, .ogg, etc.)
english.PlaylistAssocTask=Associate with &playlist files (.m3u, .m3u8, .cue, .lrc)
english.ContextMenuPlay=Play in Dawn Player
english.ContextMenuEnqueue=Enqueue in Dawn Player
english.AppRunningPrompt=Dawn Player is currently running.%nIt must be closed before installation can continue.%n%nWould you like the installer to close it automatically?
english.UninstallAppRunningPrompt=Dawn Player is currently running.%nIt must be closed before uninstall can continue.%n%nWould you like the uninstaller to close it automatically?
english.CleanAppDataPrompt=Do you want to delete all user data (settings, library DB, playlists, art cache)?%n(If unchecked, your configuration will be preserved for future installs)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:AdditionalIconsGroup}"
Name: "fileassoc"; Description: "{cm:FileAssocTask}"; GroupDescription: "{cm:OtherGroup}"
Name: "playlistassoc"; Description: "{cm:PlaylistAssocTask}"; GroupDescription: "{cm:OtherGroup}"
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:OtherGroup}"; Flags: unchecked

[Files]
; All files published from self-contained build. build-installer.ps1 injects the portable
; marker into the ZIP only, so these excludes are now belt-and-braces for a stale publish dir
; rather than the only thing keeping an installed build out of portable mode.
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.dat,portable.flag,portable,data,data\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 1. Auto-Start on Windows Boot (Optional Task)
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

; 2. ProgID Definitions for File Associations
Root: HKA; Subkey: "Software\Classes\DawnPlayer.AudioFile"; ValueType: string; ValueData: "Audio File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.AudioFile\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.AudioFile\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.AudioFile\shell\Play"; ValueType: string; ValueData: "{cm:ContextMenuPlay}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.AudioFile\shell\Play\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

Root: HKA; Subkey: "Software\Classes\DawnPlayer.PlaylistFile"; ValueType: string; ValueData: "Playlist File"; Flags: uninsdeletekey; Tasks: playlistassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.PlaylistFile\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Tasks: playlistassoc
Root: HKA; Subkey: "Software\Classes\DawnPlayer.PlaylistFile\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; 3. Audio File Extensions (.mp3, .flac, .wav, .m4a, .aac, .ogg, .oga, .alac, .opus, .m4b)
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.oga\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.alac\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.AudioFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc

; 4. Playlist File Extensions (.m3u, .m3u8, .cue, .lrc)
Root: HKA; Subkey: "Software\Classes\.m3u\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.PlaylistFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: playlistassoc
Root: HKA; Subkey: "Software\Classes\.m3u8\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.PlaylistFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: playlistassoc
Root: HKA; Subkey: "Software\Classes\.cue\OpenWithProgids"; ValueType: string; ValueName: "DawnPlayer.PlaylistFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: playlistassoc

; 5. System File Associations for Audio Context Menu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\audio\shell\DawnPlayer.Play"; ValueType: string; ValueData: "{cm:ContextMenuPlay}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\audio\shell\DawnPlayer.Play"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\audio\shell\DawnPlayer.Play\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; 6. Directory / Folder Context Menu
Root: HKA; Subkey: "Software\Classes\Directory\shell\DawnPlayer.Play"; ValueType: string; ValueData: "{cm:ContextMenuPlay}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Directory\shell\DawnPlayer.Play"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Directory\shell\DawnPlayer.Play\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc

; Directory Background Context Menu (Inside open folder)
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\DawnPlayer.Play"; ValueType: string; ValueData: "{cm:ContextMenuPlay}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\DawnPlayer.Play"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\DawnPlayer.Play\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Tasks: fileassoc

[Run]
; Launch application safely as original user after setup
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// Pascal Script for Process Management and Safe Cleanup

function IsAppProcessRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // Use tasklist to see if process exists
  Result := (Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq {#MyAppExeName}" | findstr /I "{#MyAppExeName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) and (ResultCode = 0);
end;

procedure TerminateRunningApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function EnsureAppNotRunning(const PromptMessage: string): Boolean;
var
  PromptCount: Integer;
begin
  Result := True;
  PromptCount := 0;
  
  while IsAppProcessRunning() do
  begin
    Inc(PromptCount);
    if PromptCount = 1 then
    begin
      if MsgBox(PromptMessage, mbConfirmation, MB_YESNO) = IDYES then
      begin
        TerminateRunningApp();
      end
      else
      begin
        Result := False;
        Exit;
      end;
    end
    else
    begin
      TerminateRunningApp();
    end;
    
    if not IsAppProcessRunning() then
      Break;
      
    Sleep(500);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnsureAppNotRunning(CustomMessage('AppRunningPrompt'));
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureAppNotRunning(CustomMessage('UninstallAppRunningPrompt'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\DawnPlayer');
    if DirExists(AppDataDir) then
    begin
      if MsgBox(CustomMessage('CleanAppDataPrompt'), mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(AppDataDir, True, True, True);
      end;
    end;
  end;
end;
