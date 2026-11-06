# Chatcheology.Android

The read-only Android viewer of a portable archive (design: CH-25). A thin MAUI shell over
`Chatcheology.Mobile`; Android 8.0 (API 26) and later, targeting API 36.

## Building

`dotnet build .\Chatcheology.slnx` builds it with the rest of the solution; it needs the .NET
Android workload, the Android SDK and a JDK. `dotnet build .\Chatcheology.Android -c Release`
writes `bin\Release\net10.0-android\org.chatcheology.viewer-Signed.apk`.

## Release signing

Nothing secret is in this repository. Without signing settings, a Release build still succeeds and
is signed with the Android SDK's debug key, so anyone can build it from the source.

To sign with the real key, set these MSBuild properties, either as environment variables or in
`%USERPROFILE%\.chatcheology\signing.props` (outside the repository; imported when it exists):

| Property | Meaning |
|---|---|
| `CHATCHEOLOGY_KEYSTORE` | Full path of the keystore file. Signing is used only when this is set. |
| `CHATCHEOLOGY_KEY_ALIAS` | The key's alias in the keystore. |
| `CHATCHEOLOGY_STORE_PASS` | The keystore password, as `env:NAME` or `file:PATH` (recommended) or plain text. |
| `CHATCHEOLOGY_KEY_PASS` | The key password, in the same forms. |

`env:NAME` reads the password from environment variable `NAME`, and `file:PATH` from the first line
of a file, so the password itself need not appear in any build file or log.

Example `signing.props`:

```xml
<Project>
  <PropertyGroup>
    <CHATCHEOLOGY_KEYSTORE>C:\Users\you\.chatcheology\chatcheology-upload.keystore</CHATCHEOLOGY_KEYSTORE>
    <CHATCHEOLOGY_KEY_ALIAS>chatcheology</CHATCHEOLOGY_KEY_ALIAS>
    <CHATCHEOLOGY_STORE_PASS>env:CHATCHEOLOGY_STORE_PASSWORD</CHATCHEOLOGY_STORE_PASS>
    <CHATCHEOLOGY_KEY_PASS>env:CHATCHEOLOGY_KEY_PASSWORD</CHATCHEOLOGY_KEY_PASS>
  </PropertyGroup>
</Project>
```

The keystore is the future Play Store upload key: keep it outside the repository and backed up, and
record where it is, never its passwords. `.gitignore` blocks `*.keystore`, `*.jks` and
`signing*.props` as a safety net.

## Installing on the phone

`Install-OnPhone.ps1` (beside this file) does it in one go:

```powershell
.\Chatcheology.Android\Install-OnPhone.ps1
```

1. `adb devices`: exactly one phone connected by USB with USB debugging allowed (emulators are
   ignored). If none is, it says what to do and stops. With several, pass `-Serial <serial>`.
2. Builds the Release APK (signed as above).
3. Runs `tools\Check-AndroidRelease.ps1` on it and stops if any check fails.
4. `adb install -r`, which keeps the app's imported archives and settings.

It never pushes files to the phone or reads anything from it. The archive is brought in with the
app's own "Import archive…": copy the archive folder to the phone (for example into Download), put
an empty `.nomedia` file in it so the gallery ignores its photos, then pick that folder in the app.
After the import the copied folder may be deleted. If the install reports a signature mismatch, the
app on the phone was signed with another key; uninstalling it deletes its imported archives.

## Release checks

`tools\Check-AndroidRelease.ps1` checks a built Release APK (by default
`bin\Release\net10.0-android\org.chatcheology.viewer-Signed.apk`) with the SDK's aapt2 and
zipalign: no INTERNET permission; `allowBackup` false; the backup and data-extraction rules present
and excluding every domain; only the launcher activity exported; every FileProvider not exported,
with per-URI grants, limited to `files/archives/` and `cache/shared-photos/` (CH-59); `zipalign -c -P 16` passes; the permissions are
exactly `POST_NOTIFICATIONS`, `RECEIVE_BOOT_COMPLETED` and `ACCESS_MEDIA_LOCATION` (plus
AndroidX's signature-level `DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`), any other failing. It exits 1 on any failure. Run it
after every Release build that will be installed or published. `tools\Test-CheckAndroidRelease.ps1`
runs it on small synthetic APKs and shows each refused case (INTERNET, exact alarms, a foreground
service, a storage or media-read permission, another permission, an exported receiver, and a
FileProvider that is exported, has no per-URI grants or serves any broader or other path, such as the
whole cache or another cache folder) failing.

## Privacy

A Release build has three permissions. Two are for noted-date notifications (CH-47):
`POST_NOTIFICATIONS` (asked for once on Android 13+, the first time a noted date would notify) and
`RECEIVE_BOOT_COMPLETED` (the alarm is set again after a restart). One is for import (CH-58):
`ACCESS_MEDIA_LOCATION`, asked for on Android 10+ after the folder is picked and before copying starts.
Without it Android blanks the location in photos read from the picked folder, so their hash check
fails; it gives no access to any file. With it, each file is read through the media store's address
for the same document (`MediaStore.getMediaUri`), falling back to the document address when there is
none; the hash check is unchanged, so a photo that still comes back changed fails the import. The
import goes ahead whether it is granted or refused; refused, a failed import adds one generic line
saying so (no file names). The Release log gets counts only (`mediaUri=N fallback=N`). No storage or
`READ_MEDIA_*` permission. No INTERNET (the .NET tooling adds it to Debug builds only,
for the debugger), no exact-alarm permission, no foreground service. `allowBackup` is off, and
`backup_rules.xml` and `data_extraction_rules.xml` exclude every domain, so neither cloud backup nor
device-to-device transfer copies the archive. The launcher activity is the only exported component;
the noted-date alarm and boot receivers are not exported.

A noted-date notification says only "A noted date today" (or "N noted dates today"): never a title or
the conversation's name, so nothing personal reaches a lock screen whatever its setting. The titles are
on the On this day card after the tap. One notification per archive per day; the notification time
(default 09:00) is one setting for the whole app.

The folder picker (`ACTION_OPEN_DOCUMENT_TREE`) gives the app read access to the picked folder only,
for as long as it runs; no storage permission is requested. Media is handed to other apps only
through the app's own FileProvider (`ArchiveFileProvider`): not exported, a read grant for the one
file opened, and only files under `files/archives/` plus the shared memory-photo copies in
`cache/shared-photos/` (below). MAUI Essentials' own FileProvider is removed from the manifest and its
paths resource is overridden with `files/archives/` only.

Memory photos can be shared (CH-59, D-34; this replaces the CH-46 rule that they had no Share). Share
in the memory photo viewer never hands out the archive file: the app copies it into
`cache/shared-photos/`, removes every GPS tag from the copy, checks the copy again and shares that
copy, photo only (never the caption or the memory's title). The archive file is only read, and a
photo outside `files/archives/` is refused. Old copies are deleted when the app starts and before
each share. What is done depends on the file's real format (its first bytes, not its name;
`PhotoShare` in `Chatcheology.Mobile`):

| Format | Android 8-10 (API 26-29) | Android 11+ (API 30+) |
|---|---|---|
| JPEG | GPS tags removed (`ExifInterface`) | GPS tags removed |
| PNG, WebP | copied as it is, refused if it has an EXIF block | GPS tags removed |
| GIF, BMP | copied as it is (no EXIF in the format) | copied as it is |
| HEIC/HEIF, AVIF, TIFF, raw, anything else | refused | refused |

Refused means "This photo can't be shared without its location." (missing is better than wrong). The
finished copy is also refused when it still holds location text (XMP "GPSLatitude"/"GPSLongitude"),
compressed PNG text that cannot be searched, or any GPS tag when read back. Only GPS tags are removed;
other EXIF (camera, date taken) stays. Nothing about a share is logged.

Stars (CH-38) and the On this day setting (CH-39) are kept in the app's private preferences, under the
archive's contents hash, never in the archive. Removing the archive forgets the On this day setting but
keeps the stars, so the same archive imported again has them back (a re-export starts with none). Video preview frames
(CH-40) are kept in the app's cache folder (`video-frames/`, named by media hash; Android may clear it
and they are read again). Nothing is ever written inside an installed archive.

The app logs timings only (tag `Chatcheology`: milliseconds and counts), never message text,
names, file names or paths.

## Debug builds

A Debug build compiles in the synthetic archive generator from
`Chatcheology.Mobile.Tests\Synthetic` and shows development tools on the start page (install a
synthetic archive, validate) and in the conversation page's overflow menu ("Top", "Load earlier",
with the scroll log in `adb logcat -s ChatcheologyTrial`). The synthetic memory "Invented seaside
weekend" has a sunset photo that is a JPEG with an invented camera location, for checking that Share
removes it (CH-59). A Release build contains none of it. A
Debug build uses fast deployment, so install it with `dotnet build -t:Install`, not `adb install`.
