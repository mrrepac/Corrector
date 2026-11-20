Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "${FileList}"

!define RegKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\Corrector"
!define UpdateReadyPrefix "Local\mrrepac.Corrector.UpdateReady."
Name "Corrector ${Version}"
OutFile "${SetupExe}"
InstallDir "$LOCALAPPDATA\Programs\Corrector"
SetCompressor /SOLID lzma
VIProductVersion "${Version}.0"
VIAddVersionKey /LANG=1049 ProductName "Corrector"
VIAddVersionKey /LANG=1049 ProductVersion "${Version}"
VIAddVersionKey /LANG=1049 FileVersion "${Version}"
VIAddVersionKey /LANG=1049 FileDescription "Установщик Corrector для Windows x64"
VIAddVersionKey /LANG=1049 LegalCopyright "mrrepac"
!define MUI_ICON "..\CorrectorTray.ico"
!define MUI_UNICON "..\CorrectorTray.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_DIRECTORYPAGE_TEXT_TOP "Выберите папку для Corrector. При переходе с ZIP выберите папку, где лежат Corrector.exe и assets, чтобы сохранить загруженную модель."
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Corrector.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить Corrector"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Var Work
Var PreviousDir
Var Lock
Var RestoreFailed
Var UpdatePid
Var UpdateReady
Var CanRelaunch
Var PortableUpdate

!macro LockSetup
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\mrrepac.Corrector.Setup") p.r0 ?e'
  Pop $1
  StrCpy $Lock $0
  ${If} $Lock == 0
  ${OrIf} $1 == 183
    MessageBox MB_ICONSTOP "Установка или удаление Corrector уже выполняется." /SD IDOK
    Abort
  ${EndIf}
!macroend

; Stage and back up files on the destination volume so each rename is atomic.
!macro ReplaceFile Name
  ${If} ${FileExists} "$INSTDIR\${Name}"
    ClearErrors
    Rename "$INSTDIR\${Name}" "$Work\old\${Name}"
    IfErrors rollback
  ${EndIf}
  ClearErrors
  Rename "$Work\new\${Name}" "$INSTDIR\${Name}"
  IfErrors rollback
!macroend

!macro RestoreFile Name
  ${IfNot} ${FileExists} "$Work\new\${Name}"
    ClearErrors
    Delete "$INSTDIR\${Name}"
    ${If} ${Errors}
      StrCpy $RestoreFailed 1
    ${EndIf}
  ${EndIf}
  ${If} ${FileExists} "$Work\old\${Name}"
    ClearErrors
    Rename "$Work\old\${Name}" "$INSTDIR\${Name}"
    ${If} ${Errors}
      StrCpy $RestoreFailed 1
    ${EndIf}
  ${EndIf}
!macroend

Section "Corrector" App
  SectionIn RO
  Call CheckDirectory
  Pop $0
  ${If} $0 != ""
    MessageBox MB_ICONSTOP "$0" /SD IDOK
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  ClearErrors
  GetTempFileName $Work "$INSTDIR"
  IfErrors storageFailure
  Delete "$Work"
  ClearErrors
  CreateDirectory "$Work\old\licenses"
  IfErrors extractFailure
  SetOutPath "$Work\new"
  File /r "${PayloadDir}\*.*"
  IfErrors extractFailure
  ${If} $PortableUpdate != 1
    WriteUninstaller "$Work\new\Uninstall-Corrector.exe"
    IfErrors extractFailure
  ${EndIf}
  SetOutPath "$INSTDIR"
  CreateDirectory "$INSTDIR\licenses"
  IfErrors extractFailure
  !insertmacro Payload ReplaceFile
  ${If} $PortableUpdate != 1
    !insertmacro ReplaceFile "Uninstall-Corrector.exe"
  ${EndIf}
  RMDir /r "$Work"

  ${If} $UpdatePid != ""
    ${If} $PortableUpdate != 1
      WriteRegStr HKCU "${RegKey}" "DisplayVersion" "${Version}"
      WriteRegStr HKCU "${RegKey}" "DisplayName" "Corrector"
    ${EndIf}
    Goto done
  ${EndIf}
  WriteRegStr HKCU "${RegKey}" "DisplayName" "Corrector"
  WriteRegStr HKCU "${RegKey}" "DisplayVersion" "${Version}"
  WriteRegStr HKCU "${RegKey}" "Publisher" "mrrepac"
  WriteRegStr HKCU "${RegKey}" "DisplayIcon" "$INSTDIR\Corrector.exe,0"
  WriteRegStr HKCU "${RegKey}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${RegKey}" "UninstallString" '"$INSTDIR\Uninstall-Corrector.exe"'
  WriteRegStr HKCU "${RegKey}" "QuietUninstallString" '"$INSTDIR\Uninstall-Corrector.exe" /S'
  WriteRegDWORD HKCU "${RegKey}" "NoModify" 1
  WriteRegDWORD HKCU "${RegKey}" "NoRepair" 1
  CreateShortcut "$SMPROGRAMS\Corrector.lnk" "$INSTDIR\Corrector.exe"
  Goto done

rollback:
  StrCpy $RestoreFailed 0
  !insertmacro Payload RestoreFile
  ${If} $PortableUpdate != 1
    !insertmacro RestoreFile "Uninstall-Corrector.exe"
  ${EndIf}
  ${If} $RestoreFailed == 1
    MessageBox MB_ICONSTOP "Не удалось восстановить файлы. Резервная копия сохранена в $Work\old." /SD IDOK
    Abort
  ${EndIf}
  RMDir /r "$Work"
  MessageBox MB_ICONSTOP "Файлы заняты или недоступны для записи. Закройте Corrector через меню в трее и повторите установку. Прежние файлы восстановлены." /SD IDOK
  Abort
extractFailure:
  SetOutPath "$TEMP"
  RMDir /r "$Work"
storageFailure:
  MessageBox MB_ICONSTOP "Не удалось записать файлы. Проверьте свободное место и права на папку." /SD IDOK
  Abort
done:
SectionEnd

Section "Ярлык на рабочем столе" Desktop
  CreateShortcut "$DESKTOP\Corrector.lnk" "$INSTDIR\Corrector.exe"
SectionEnd

Section -Finish
  ${If} $UpdatePid != ""
    Return
  ${EndIf}
  ${If} ${SectionIsSelected} ${Desktop}
    WriteRegDWORD HKCU "${RegKey}" "DesktopShortcut" 1
  ${Else}
    ReadRegDWORD $0 HKCU "${RegKey}" "DesktopShortcut"
    ${If} $0 == 1
      Delete "$DESKTOP\Corrector.lnk"
    ${EndIf}
    WriteRegDWORD HKCU "${RegKey}" "DesktopShortcut" 0
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${App} "Программа и ярлык в меню «Пуск». Модель загружается при первом запуске."
  !insertmacro MUI_DESCRIPTION_TEXT ${Desktop} "Быстрый запуск Corrector с рабочего стола."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function SignalReadyAndWaitForParent
  System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i $UpdatePid) p.r2'
  ${If} $2 == 0
    Abort
  ${EndIf}
  ; The client owns this event. A missing event leaves its process running.
  System::Call 'kernel32::OpenEventW(i 2, i 0, w "$UpdateReady") p.r3'
  ${If} $3 == 0
    System::Call 'kernel32::CloseHandle(p r2)'
    Abort
  ${EndIf}
  System::Call 'kernel32::SetEvent(p r3) i.r4'
  System::Call 'kernel32::CloseHandle(p r3)'
  ${If} $4 == 0
    System::Call 'kernel32::CloseHandle(p r2)'
    Abort
  ${EndIf}
  ; The client may finish a newly started correction before exiting.
  System::Call 'kernel32::WaitForSingleObject(p r2, i -1) i.r4'
  System::Call 'kernel32::CloseHandle(p r2)'
  ${If} $4 != 0
    Abort
  ${EndIf}
FunctionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Для Corrector требуется 64-разрядная Windows." /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext current
  ReadRegStr $PreviousDir HKCU "${RegKey}" "InstallLocation"
  ${GetParameters} $0
  ${GetOptions} $0 "/UPDATEPID=" $UpdatePid
  ClearErrors
  ${GetOptions} $0 "/UPDATEREADY=" $UpdateReady
  ${IfNot} ${Errors}
    StrLen $1 "${UpdateReadyPrefix}"
    StrCpy $2 $UpdateReady $1
    StrCpy $3 $UpdateReady "" $1
    StrLen $4 $3
    System::Call 'shlwapi::StrSpnW(w r3, w "0123456789abcdefABCDEF") i.r5'
    ${If} $UpdatePid == ""
    ${OrIf} $2 S!= "${UpdateReadyPrefix}"
    ${OrIf} $4 != 32
    ${OrIf} $5 != 32
      Abort
    ${EndIf}
  ${EndIf}
  ${If} $UpdatePid != ""
    ${IfNot} ${FileExists} "$INSTDIR\Corrector.exe"
    ${OrIfNot} ${FileExists} "$INSTDIR\assets.lock.json"
      Abort
    ${EndIf}
    ${If} $PreviousDir != $INSTDIR
      StrCpy $PortableUpdate 1
    ${EndIf}
    Call CheckDirectory
    Pop $0
    ${If} $0 != ""
      Abort
    ${EndIf}
    ${If} $UpdateReady == ""
      ; Older clients exit immediately after launching the installer.
      System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i $UpdatePid) p.r0'
      ${If} $0 != 0
        System::Call 'kernel32::WaitForSingleObject(p r0, i 60000) i.r1'
        System::Call 'kernel32::CloseHandle(p r0)'
        ${If} $1 != 0
          Abort
        ${EndIf}
      ${EndIf}
    ${EndIf}
    !insertmacro UnselectSection ${Desktop}
  ${EndIf}
  !insertmacro LockSetup
  ${If} $UpdatePid != ""
    ${If} $UpdateReady != ""
      Call SignalReadyAndWaitForParent
    ${EndIf}
    StrCpy $CanRelaunch 1
    Return
  ${EndIf}
  ${If} $PreviousDir != ""
    StrCpy $INSTDIR "$PreviousDir"
    ReadRegDWORD $0 HKCU "${RegKey}" "DesktopShortcut"
    ${If} $0 == 0
      !insertmacro UnselectSection ${Desktop}
    ${EndIf}
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  ${If} $UpdatePid != ""
    Exec '"$INSTDIR\Corrector.exe" --background'
  ${EndIf}
FunctionEnd

Function .onInstFailed
  ${If} $CanRelaunch == 1
  ${AndIf} $RestoreFailed != 1
    Exec '"$INSTDIR\Corrector.exe" --update-failed'
  ${EndIf}
FunctionEnd

!macro CheckCollision Name
  ${If} ${FileExists} "$INSTDIR\${Name}"
    Push "В папке уже есть файл ${Name} другой установки."
    Return
  ${EndIf}
!macroend

Function CheckDirectory
  ${GetRoot} "$INSTDIR" $0
  ${If} "$INSTDIR" == "$0\"
  ${OrIf} "$INSTDIR" == "$0"
    Push "Выберите папку программы внутри диска."
    Return
  ${EndIf}
  ${If} $PreviousDir != ""
  ${AndIf} $INSTDIR != $PreviousDir
  ${AndIf} $PortableUpdate != 1
    Push "Для обновления выберите прежнюю папку: $PreviousDir"
    Return
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\Corrector.exe"
  ${OrIfNot} ${FileExists} "$INSTDIR\assets.lock.json"
    ; An uninstall intentionally leaves downloaded models behind. Reuse such
    ; a directory if none of the files we would install are already present.
    ${If} ${FileExists} "$INSTDIR\assets\*.*"
      !insertmacro Payload CheckCollision
      !insertmacro CheckCollision "Uninstall-Corrector.exe"
      Push ""
      Return
    ${EndIf}
    FindFirst $0 $1 "$INSTDIR\*"
    ${DoWhile} $1 != ""
      ${If} $1 != "."
      ${AndIf} $1 != ".."
        FindClose $0
        Push "Выберите пустую папку или папку с переносимой версией Corrector."
        Return
      ${EndIf}
      FindNext $0 $1
    ${Loop}
    FindClose $0
  ${EndIf}
  Push ""
FunctionEnd

Function .onVerifyInstDir
  Call CheckDirectory
  Pop $0
  ${If} $0 != ""
    Abort
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro LockSetup
  SetRegView 64
  SetShellVarContext current
  ReadRegStr $0 HKCU "${RegKey}" "InstallLocation"
  ${If} $0 != $INSTDIR
    MessageBox MB_ICONSTOP "Запустите удаление Corrector из списка установленных приложений Windows." /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

!macro DeleteInstalled Name
  ${If} ${FileExists} "$INSTDIR\${Name}"
    ClearErrors
    Delete "$INSTDIR\${Name}"
    IfErrors busy
  ${EndIf}
!macroend

Section "Uninstall"
  ; Check the running binary before removing its libraries. Only packaged files
  ; are removed: downloaded models, settings and unrelated files are retained.
  !insertmacro DeleteInstalled "Corrector.exe"
  !insertmacro Payload DeleteInstalled
  !insertmacro DeleteInstalled "Uninstall-Corrector.exe"
  Delete "$SMPROGRAMS\Corrector.lnk"
  ReadRegDWORD $0 HKCU "${RegKey}" "DesktopShortcut"
  ${If} $0 == 1
    Delete "$DESKTOP\Corrector.lnk"
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ABC-Corrector"
  ${If} $0 == '"$INSTDIR\Corrector.exe" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ABC-Corrector"
  ${EndIf}
  DeleteRegKey HKCU "${RegKey}"
  RMDir "$INSTDIR\licenses"
  RMDir "$INSTDIR"
  Goto done
busy:
  MessageBox MB_ICONSTOP "Закройте Corrector через меню в трее и повторите удаление." /SD IDOK
  Abort
done:
SectionEnd
