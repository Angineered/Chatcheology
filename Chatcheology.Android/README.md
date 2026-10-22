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

## Privacy

No permissions in a Release build (the .NET tooling adds INTERNET to Debug builds only, for the
debugger). `allowBackup` is off, and `backup_rules.xml` and `data_extraction_rules.xml` exclude every
domain, so neither cloud backup nor device-to-device transfer copies the archive. The launcher
activity is the only exported component.

## Debug builds

A Debug build compiles in the synthetic archive generator from
`Chatcheology.Mobile.Tests\Synthetic` and shows development tools on the start page (install a
synthetic archive, validate) and on the conversation page ("Top", "Load earlier", with the scroll
log in `adb logcat -s ChatcheologyTrial`). A Release build contains none of it. The conversation
list is MAUI's CollectionView, as the CH-26 trial chose; the native RecyclerView trial was removed.
