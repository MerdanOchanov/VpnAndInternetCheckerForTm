' build-msi.vbs - authors InternetChecker.msi via the Windows Installer COM API.
' Usage: cscript //nologo build-msi.vbs <msiPath> <cabPath> <srcDir> <version>
' Files embedded (cab members must match these File keys): iccexe, icccfg, iccrdme.
' Includes: per-machine install, Start Menu shortcut, major-upgrade (removes old
' version) and a taskkill custom action that closes a running old instance first.

Option Explicit

Const msiOpenDatabaseModeCreate = 3
Const msiViewModifyInsert = 1

Dim args, msiPath, cabPath, srcDir, ver
Set args = WScript.Arguments
msiPath = args(0) : cabPath = args(1) : srcDir = args(2) : ver = args(3)

Dim fso : Set fso = CreateObject("Scripting.FileSystemObject")
If fso.FileExists(msiPath) Then fso.DeleteFile msiPath

Dim exePath, cfgPath, rdmePath
exePath = fso.BuildPath(srcDir, "InternetChecker.exe")
cfgPath = fso.BuildPath(srcDir, "internetchecker.cfg")
rdmePath = fso.BuildPath(srcDir, "README.md")

Dim exeSize, cfgSize, rdmeSize
exeSize = fso.GetFile(exePath).Size
cfgSize = fso.GetFile(cfgPath).Size
rdmeSize = fso.GetFile(rdmePath).Size

Dim upgradeCode : upgradeCode = "{B7E5C3A1-6F2D-4E8B-9C1A-2D3E4F5A6B7C}"
Dim productCode : productCode = NewGuid()
Dim packageCode : packageCode = NewGuid()

Dim installer : Set installer = CreateObject("WindowsInstaller.Installer")
Dim db : Set db = installer.OpenDatabase(msiPath, msiOpenDatabaseModeCreate)

' ---- schema ----
Q db, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL PRIMARY KEY `Property`)"
Q db, "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)"
Q db, "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)"
Q db, "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)"
Q db, "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)"
Q db, "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)"
Q db, "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)"
Q db, "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)"
Q db, "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)"
Q db, "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)"
Q db, "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)"
Q db, "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)"

' ---- Property ----
Ins db, "Property", Array("ProductName", "InternetChecker")
Ins db, "Property", Array("ProductCode", productCode)
Ins db, "Property", Array("ProductVersion", ver)
Ins db, "Property", Array("ProductLanguage", "1033")
Ins db, "Property", Array("Manufacturer", "InternetChecker")
Ins db, "Property", Array("UpgradeCode", upgradeCode)
Ins db, "Property", Array("ALLUSERS", "1")
Ins db, "Property", Array("MSIRESTARTMANAGERCONTROL", "Disable")
Ins db, "Property", Array("ARPNOMODIFY", "1")

' ---- Directory ----
Ins db, "Directory", Array("TARGETDIR", "", "SourceDir")
Ins db, "Directory", Array("ProgramFilesFolder", "TARGETDIR", ".")
Ins db, "Directory", Array("INSTALLDIR", "ProgramFilesFolder", "IntChk|InternetChecker")
Ins db, "Directory", Array("ProgramMenuFolder", "TARGETDIR", ".")
Ins db, "Directory", Array("SystemFolder", "TARGETDIR", ".")

' ---- Feature / Component ----
Ins db, "Feature", Array("MainFeature", "", "InternetChecker", "Core files", 1, 1, "INSTALLDIR", 0)
Ins db, "Component", Array("MainComp", "{2C1B9A7E-3D4F-4A5B-8C6D-7E8F9A0B1C2D}", "INSTALLDIR", 0, "", "iccexe")
Ins db, "FeatureComponents", Array("MainFeature", "MainComp")

' ---- Files (order matches cab members) ----
Ins db, "File", Array("iccexe", "MainComp", "IntChk.exe|InternetChecker.exe", exeSize, "", "", 512, 1)
Ins db, "File", Array("icccfg", "MainComp", "intchk.cfg|internetchecker.cfg", cfgSize, "", "", 512, 2)
Ins db, "File", Array("iccrdme", "MainComp", "README.md", rdmeSize, "", "", 512, 3)

Ins db, "Media", Array(1, 3, "", "#app.cab", "", "")

' ---- Shortcut (Start Menu) ----
Ins db, "Shortcut", Array("IccSc", "ProgramMenuFolder", "IntChk|InternetChecker", "MainComp", "[#iccexe]", "", "Internet and VPN checker", Null, Null, Null, 1, "INSTALLDIR")

' ---- Major upgrade: remove any version <= current, then install ----
Ins db, "Upgrade", Array(upgradeCode, "", ver, "", 256, "", "OLDVERSIONS")

' ---- Custom action: close a running instance before install ----
' Type 34 (exe from directory) + 64 (ignore return code) = 98
Ins db, "CustomAction", Array("KillRunning", 98, "SystemFolder", "taskkill.exe /F /IM InternetChecker.exe")

' ---- Sequences ----
Ins db, "InstallExecuteSequence", Array("FindRelatedProducts", "", 25)
Ins db, "InstallExecuteSequence", Array("CostInitialize", "", 800)
Ins db, "InstallExecuteSequence", Array("FileCost", "", 900)
Ins db, "InstallExecuteSequence", Array("CostFinalize", "", 1000)
Ins db, "InstallExecuteSequence", Array("KillRunning", "", 1250)
Ins db, "InstallExecuteSequence", Array("InstallValidate", "", 1400)
Ins db, "InstallExecuteSequence", Array("InstallInitialize", "", 1500)
Ins db, "InstallExecuteSequence", Array("RemoveExistingProducts", "", 1550)
Ins db, "InstallExecuteSequence", Array("ProcessComponents", "", 1600)
Ins db, "InstallExecuteSequence", Array("UnpublishFeatures", "", 1800)
Ins db, "InstallExecuteSequence", Array("RemoveShortcuts", "", 3200)
Ins db, "InstallExecuteSequence", Array("RemoveFiles", "", 3500)
Ins db, "InstallExecuteSequence", Array("InstallFiles", "", 4000)
Ins db, "InstallExecuteSequence", Array("CreateShortcuts", "", 4500)
Ins db, "InstallExecuteSequence", Array("RegisterProduct", "", 6100)
Ins db, "InstallExecuteSequence", Array("PublishFeatures", "", 6300)
Ins db, "InstallExecuteSequence", Array("PublishProduct", "", 6400)
Ins db, "InstallExecuteSequence", Array("InstallFinalize", "", 6600)

Ins db, "InstallUISequence", Array("FindRelatedProducts", "", 25)
Ins db, "InstallUISequence", Array("CostInitialize", "", 800)
Ins db, "InstallUISequence", Array("FileCost", "", 900)
Ins db, "InstallUISequence", Array("CostFinalize", "", 1000)
Ins db, "InstallUISequence", Array("ExecuteAction", "", 1300)

' ---- embed the cab as a stream (SELECT view + Modify Insert is the reliable pattern) ----
Dim v : Set v = db.OpenView("SELECT `Name`,`Data` FROM `_Streams`")
v.Execute Nothing
Dim rec : Set rec = installer.CreateRecord(2)
rec.StringData(1) = "app.cab"
rec.SetStream 2, cabPath
v.Modify msiViewModifyInsert, rec
v.Close

' ---- summary information ----
Dim si : Set si = db.SummaryInformation(20)
si.Property(1) = 1252
si.Property(2) = "InternetChecker"
si.Property(3) = "InternetChecker " & ver
si.Property(4) = "InternetChecker"
si.Property(5) = "Installer"
si.Property(6) = "Internet and VPN checker for Turkmenistan"
si.Property(7) = "Intel;1033"
si.Property(9) = packageCode
si.Property(14) = 200
si.Property(15) = 2
si.Property(18) = "InternetChecker build-msi.vbs"
si.Property(19) = 0
si.Persist

db.Commit
WScript.Echo "MSI created: " & msiPath
WScript.Echo "ProductCode=" & productCode & "  Version=" & ver
WScript.Quit 0

' ============ helpers ============
Sub Q(database, sql)
    Dim view : Set view = database.OpenView(sql)
    view.Execute Nothing
    view.Close
End Sub

Sub Ins(database, table, vals)
    Dim cols : cols = ColList(table)
    Dim vlist : vlist = ""
    Dim i, val
    For i = 0 To UBound(vals)
        If i > 0 Then vlist = vlist & ","
        val = vals(i)
        If IsNull(val) Then
            vlist = vlist & "NULL"
        ElseIf VarType(val) = vbString Then
            If Len(val) = 0 Then
                vlist = vlist & "NULL"
            Else
                vlist = vlist & "'" & Replace(val, "'", "''") & "'"
            End If
        Else
            vlist = vlist & CStr(CLng(val))
        End If
    Next
    On Error Resume Next
    Dim view : Set view = database.OpenView("INSERT INTO `" & table & "` (" & cols & ") VALUES (" & vlist & ")")
    view.Execute Nothing
    If Err.Number <> 0 Then
        WScript.Echo "FAILED insert into " & table & ": " & Err.Description & "  SQL VALUES(" & vlist & ")"
        WScript.Quit 2
    End If
    On Error GoTo 0
    view.Close
End Sub

Function ColList(table)
    Select Case table
        Case "Property" : ColList = "`Property`,`Value`"
        Case "Directory" : ColList = "`Directory`,`Directory_Parent`,`DefaultDir`"
        Case "Feature" : ColList = "`Feature`,`Feature_Parent`,`Title`,`Description`,`Display`,`Level`,`Directory_`,`Attributes`"
        Case "Component" : ColList = "`Component`,`ComponentId`,`Directory_`,`Attributes`,`Condition`,`KeyPath`"
        Case "FeatureComponents" : ColList = "`Feature_`,`Component_`"
        Case "File" : ColList = "`File`,`Component_`,`FileName`,`FileSize`,`Version`,`Language`,`Attributes`,`Sequence`"
        Case "Media" : ColList = "`DiskId`,`LastSequence`,`DiskPrompt`,`Cabinet`,`VolumeLabel`,`Source`"
        Case "InstallExecuteSequence" : ColList = "`Action`,`Condition`,`Sequence`"
        Case "InstallUISequence" : ColList = "`Action`,`Condition`,`Sequence`"
        Case "CustomAction" : ColList = "`Action`,`Type`,`Source`,`Target`"
        Case "Upgrade" : ColList = "`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`"
        Case "Shortcut" : ColList = "`Shortcut`,`Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`Hotkey`,`Icon_`,`IconIndex`,`ShowCmd`,`WkDir`"
    End Select
End Function

Function NewGuid()
    Dim g : g = CreateObject("Scriptlet.TypeLib").Guid
    g = Replace(g, vbCr, "") : g = Replace(g, vbLf, "") : g = Replace(g, Chr(0), "") : g = Trim(g)
    Dim a, b : a = InStr(g, "{") : b = InStr(g, "}")
    If a > 0 And b > a Then g = Mid(g, a, b - a + 1)
    NewGuid = g
End Function
