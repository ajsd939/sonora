; Sonora for Windows - installer.
;
; makensis reads every relative path here against this file's own directory, so the
; script can be compiled from anywhere:
;
;   dotnet publish Sonora.csproj -f net10.0-windows10.0.19041.0 -c Release ^
;       --no-restore -p:WindowsPackageType=None -p:PublishReadyToRun=false
;   makensis installer\sonora.nsi
;
; The payload is the self-contained publish output, so nothing on the target machine
; needs the .NET runtime installed first.

!ifndef VERSION
	!define VERSION "0.9.1"
!endif

!ifndef PUBLISH_DIR
	!define PUBLISH_DIR "..\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
!endif

!define APP_NAME "Sonora"
!define APP_EXE "Sonora.exe"
!define PUBLISHER "ajsd939"
!define PROJECT_URL "https://github.com/ajsd939/sonora"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define APP_KEY "Software\${APP_NAME}"
!define WEBVIEW2_KEY "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

Name "${APP_NAME} ${VERSION}"
OutFile "Sonora-${VERSION}-win-x64.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "${APP_KEY}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64

; Four numeric parts, whatever the label next to it says.
VIProductVersion "0.9.1.0"
VIAddVersionKey /LANG=0 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=0 "FileDescription" "${APP_NAME} setup"
VIAddVersionKey /LANG=0 "LegalCopyright" "GPL-3.0-only"

; Same mark as Sonora.exe; makensis anchors this to the script directory, not the cwd.
!define MUI_ICON "..\Resources\AppIcon\appicon.ico"
!define MUI_UNICON "..\Resources\AppIcon\appicon.ico"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "${APP_NAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Songs, albums and artists on your machine, played through a page rather than a web view of someone else's player.$\r$\n$\r$\nThis is a beta: the library, the playlists and the retry logic all work, but they have not been lived with long enough to be called finished.$\r$\n$\r$\nIt is GPL-3.0 software and it installs for every user on this computer."
!insertmacro MUI_PAGE_WELCOME

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME}"
!define MUI_FINISHPAGE_SHOWREADME "${PROJECT_URL}"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the project page"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Function .onInit
	; The whole interface is a WebView2 page: without the runtime the window opens and
	; stays white, which is a far worse surprise than one more dialog here.
	ReadRegStr $0 HKLM "${WEBVIEW2_KEY}" "pv"
	${If} $0 == ""
		ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
	${EndIf}
	${If} $0 == ""
		ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
	${EndIf}
	${If} $0 == ""
		MessageBox MB_OK|MB_ICONEXCLAMATION \
			"${APP_NAME} draws its interface with the Microsoft Edge WebView2 runtime, which does not appear to be installed.$\r$\n$\r$\nInstallation will continue, but the app needs the runtime before it can show anything. It comes with recent Edge and Chrome installs, and can be downloaded from microsoft.com."
	${EndIf}
FunctionEnd

Section "!${APP_NAME}" SecApp
	SetOutPath "$INSTDIR"
	File /r "${PUBLISH_DIR}\*.*"

	${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
	IntFmt $0 "0x%08X" $0

	WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
	WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
	WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
	WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "${PROJECT_URL}"
	WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
	WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
	WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" $0
	WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
	WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
	WriteRegStr HKLM "${APP_KEY}" "InstallDir" "$INSTDIR"
	WriteRegStr HKLM "${APP_KEY}" "Version" "${VERSION}"

	WriteUninstaller "$INSTDIR\Uninstall.exe"

	CreateDirectory "$SMPROGRAMS\${APP_NAME}"
	CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
	CreateShortCut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "Desktop shortcut" SecDesktop
	CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
	!insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "The player, its web assets and the .NET runtime it carries with it."
	!insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Put ${APP_NAME} on the desktop as well as in the Start menu."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section Uninstall
	Delete "$DESKTOP\${APP_NAME}.lnk"
	Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
	Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
	RMDir "$SMPROGRAMS\${APP_NAME}"

	DeleteRegKey HKLM "${UNINST_KEY}"
	DeleteRegKey HKLM "${APP_KEY}"

	; Only take the folder back when it really is ours, because the listener may have
	; pointed the installer at a directory holding something else.
	Delete "$INSTDIR\Uninstall.exe"
	IfFileExists "$INSTDIR\${APP_EXE}" 0 +2
		RMDir /r "$INSTDIR"
	RMDir "$INSTDIR"
SectionEnd
