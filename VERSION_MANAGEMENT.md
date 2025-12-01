# Version Management Guide

This document explains how to manage version numbers and build times in the SerialPortTool application.

## Quick Start

### Updating the Version Number

1. **Edit [`version.json`](version.json:1)** to change the version:
   ```json
   {
     "version": "1.2.3",
     "description": "SerialPortTool Version Configuration",
     "changelog": [
       {
         "version": "1.2.3",
         "date": "2025-12-01",
         "changes": [
           "Your new features here",
           "Bug fixes here"
         ]
       }
     ]
   }
   ```

2. **Update [`SerialPortTool.csproj`](SerialPortTool.csproj:17)** to match:
   ```xml
   <Version>1.2.3</Version>
   <AssemblyVersion>1.2.3.0</AssemblyVersion>
   <FileVersion>1.2.3.0</FileVersion>
   <InformationalVersion>1.2.3+$([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))</InformationalVersion>
   ```

3. **Build the project** - the build time will be automatically captured:
   ```bash
   dotnet build
   ```

## How It Works

### Automatic Build Time

The build time is **automatically generated** at compile time using MSBuild property functions:

- **Source**: [`SerialPortTool.csproj`](SerialPortTool.csproj:20) line 20
- **Format**: `yyyy-MM-dd HH:mm:ss` (e.g., "2025-12-01 16:03:10")
- **Storage**: Embedded in `AssemblyInformationalVersionAttribute`

Every time you run `dotnet build`, the current timestamp is captured and embedded into the compiled assembly.

### Version Display

The version and build time are displayed in two locations:

1. **Window Title**: [`MainViewModel.cs`](ViewModels/MainViewModel.cs:90)
   - Shows: "串口工具 - Multi-Port Serial Monitor v1.0.0 (Build: 2025-12-01 16:03:10)"

2. **Status Bar**: [`MainWindow.xaml`](MainWindow.xaml:260)
   - Bottom-right corner with subtle styling
   - Format: "v1.0.0 (Build: 2025-12-01 16:03:10)"

### Version Info Helper

The [`VersionInfo`](Helpers/VersionInfo.cs:10) class provides:

- **[`Version`](Helpers/VersionInfo.cs:18)**: Short version string (e.g., "1.0.0")
- **[`FullVersion`](Helpers/VersionInfo.cs:29)**: Complete version with revision (e.g., "1.0.0.0")
- **[`BuildTime`](Helpers/VersionInfo.cs:40)**: Extracted from assembly or PE header
- **[`VersionString`](Helpers/VersionInfo.cs:68)**: Combined display format

## Version Update Workflow

### For Releases

1. **Update version.json**:
   ```json
   {
     "version": "1.1.0",
     "changelog": [
       {
         "version": "1.1.0",
         "date": "2025-12-15",
         "changes": [
           "Added new feature X",
           "Fixed bug Y",
           "Improved performance Z"
         ]
       }
     ]
   }
   ```

2. **Update csproj** to match the version in version.json

3. **Build** - build time is auto-generated

4. **Commit** both files together:
   ```bash
   git add version.json SerialPortTool.csproj
   git commit -m "Bump version to 1.1.0"
   git tag v1.1.0
   ```

### Version Number Format

Use [Semantic Versioning](https://semver.org/):

- **MAJOR** version: Incompatible API changes
- **MINOR** version: Add functionality (backwards-compatible)
- **PATCH** version: Bug fixes (backwards-compatible)

Example: `1.2.3` = Major.Minor.Patch

## Files Involved

| File | Purpose | Auto-Generated |
|------|---------|----------------|
| [`version.json`](version.json:1) | Version tracking & changelog | ❌ Manual |
| [`SerialPortTool.csproj`](SerialPortTool.csproj:17) | Build-time version metadata | ❌ Manual (version)<br>✅ Auto (build time) |
| [`Helpers/VersionInfo.cs`](Helpers/VersionInfo.cs:1) | Runtime version extraction | ✅ Auto (reads from assembly) |
| [`ViewModels/MainViewModel.cs`](ViewModels/MainViewModel.cs:93) | Exposes version to UI | ✅ Auto (uses VersionInfo) |
| [`MainWindow.xaml`](MainWindow.xaml:260) | Displays version in UI | ✅ Auto (binds to ViewModel) |

## Best Practices

1. **Always update both files together**: Keep version.json and .csproj in sync
2. **Build time is automatic**: Never manually edit build timestamps
3. **Document changes**: Use version.json changelog to track what changed
4. **Tag releases**: Use git tags to mark version milestones
5. **Follow SemVer**: Use semantic versioning for predictable upgrades

## Troubleshooting

### Build time shows wrong timezone
- Build time is automatically converted to local timezone
- The PE header timestamp is in UTC, converted in [`GetBuildDateTime()`](Helpers/VersionInfo.cs:70)

### Version doesn't update after changing csproj
- Clean and rebuild: `dotnet clean && dotnet build`
- Check that MSBuild is using the correct property values

### Version.json not being read
- Ensure version.json is in the project root directory
- Check that it's included in the build: [`SerialPortTool.csproj`](SerialPortTool.csproj:25) lines 25-28

## Automated Release with GitHub Actions

The project includes automated build and release workflows using GitHub Actions.

### Automated Release Workflow

When you push a version tag (e.g., `v1.2.3`) to GitHub, the workflow automatically:

1. **Builds** the application for both x64 and ARM64 platforms
2. **Verifies** that version.json matches the tag version
3. **Packages** the compiled application into ZIP files
4. **Generates** release notes from version.json changelog
5. **Creates** a GitHub Release with all build artifacts

**Workflow File**: [`.github/workflows/release.yml`](.github/workflows/release.yml:1)

### Quick Release Process (Automated)

Use the version bump script for the easiest release process:

```powershell
# Bump patch version (1.0.0 → 1.0.1)
.\scripts\bump-version.ps1 -BumpType patch -Message "Bug fixes and improvements"

# Bump minor version (1.0.1 → 1.1.0)
.\scripts\bump-version.ps1 -BumpType minor -Message "Added new features"

# Bump major version (1.1.0 → 2.0.0)
.\scripts\bump-version.ps1 -BumpType major -Message "Breaking changes"

# Push to trigger the automated release
git push && git push --tags
```

The script automatically:
- Updates version.json and SerialPortTool.csproj
- Creates a changelog entry with your message
- Commits the changes
- Creates and tags the version

Once you push the tag, GitHub Actions takes over and builds/releases everything automatically.

### Manual Release Process

If you prefer manual control:

```powershell
# 1. Update version files manually
.\scripts\bump-version.ps1 -BumpType patch -NoCommit

# 2. Review changes
git diff version.json SerialPortTool.csproj

# 3. Commit and tag
git add version.json SerialPortTool.csproj
git commit -m "Bump version to 1.2.3"
git tag -a v1.2.3 -m "Release 1.2.3"

# 4. Push to trigger automated build
git push origin main
git push origin v1.2.3
```

### Helper Scripts

| Script | Purpose | Usage |
|--------|---------|-------|
| [`scripts/bump-version.ps1`](scripts/bump-version.ps1:1) | Automate version bumping and tagging | `.\scripts\bump-version.ps1 -BumpType [major\|minor\|patch]` |
| [`scripts/generate-release-notes.ps1`](scripts/generate-release-notes.ps1:1) | Generate release notes from changelog | `.\scripts\generate-release-notes.ps1 -Version "1.2.3"` |

### Build Artifacts

Each release automatically includes:

- **SerialPortTool-vX.X.X-win-x64.zip**: For 64-bit Windows PCs
- **SerialPortTool-vX.X.X-win-ARM64.zip**: For ARM-based Windows devices

All packages are:
- Self-contained (no .NET installation required)
- Single-file executables with all dependencies included
- Compressed for smaller download size

### Monitoring Releases

1. **Check workflow status**: Visit the Actions tab in your GitHub repository
2. **View build logs**: Click on a workflow run to see detailed logs
3. **Download artifacts**: Build artifacts are available for 30 days
4. **Release notes**: Automatically generated from version.json changelog

### Troubleshooting Releases

#### Workflow fails with "Version mismatch"
- Ensure version.json version matches the git tag
- The tag should be `v1.2.3` and version.json should have `"version": "1.2.3"`

#### Build fails on specific platform
- Check the Actions logs for the specific platform (x64 or ARM64)
- Verify all NuGet packages support the target platform

#### Release not created
- Ensure you pushed both the commit AND the tag
- Check that the tag follows the pattern `v*` (e.g., `v1.2.3`)
- Verify GitHub Actions has write permissions for releases

## Future Enhancements

Consider these improvements:

1. **Automatic version bumping**: PowerShell script to increment version numbers
2. **Git-based versioning**: Extract version from git tags automatically
3. **CI/CD integration**: Inject version from build pipeline
4. **Version comparison**: Add "Check for Updates" feature

## References

- [Semantic Versioning 2.0.0](https://semver.org/)
- [.NET Assembly Versioning](https://learn.microsoft.com/en-us/dotnet/standard/assembly/versioning)
- [MSBuild Property Functions](https://learn.microsoft.com/en-us/visualstudio/msbuild/property-functions)