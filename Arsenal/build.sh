#!/bin/bash

# ARSENAL Mod - Mac Build Script (Simplified)

echo "=========================================="
echo "  ARSENAL Mod - macOS Build Script"
echo "=========================================="
echo ""

# Your specific RimWorld path
RIMWORLD_MANAGED="/Users/johnshaffer/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed"

# Project paths (run this script from the Arsenal mod folder)
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$PROJECT_DIR/Source/Arsenal"
ASSEMBLIES_DIR="$PROJECT_DIR/Assemblies"

echo "Project Dir: $PROJECT_DIR"
echo "Source Dir: $SOURCE_DIR"
echo "RimWorld Managed: $RIMWORLD_MANAGED"
echo ""

# Verify RimWorld path
echo "Checking RimWorld installation..."
if [ ! -f "$RIMWORLD_MANAGED/Assembly-CSharp.dll" ]; then
    echo "❌ ERROR: Assembly-CSharp.dll not found at:"
    echo "   $RIMWORLD_MANAGED"
    echo ""
    echo "Let's find it. Running search..."
    find /Users/johnshaffer -name "Assembly-CSharp.dll" 2>/dev/null
    echo ""
    echo "Update RIMWORLD_MANAGED in this script with the correct path."
    exit 1
fi
echo "✅ RimWorld found!"
echo ""

# Check for .NET SDK
echo "Checking for .NET SDK..."
if ! command -v dotnet &> /dev/null; then
    echo "❌ ERROR: .NET SDK not found!"
    echo "Install it with: brew install dotnet-sdk"
    echo "Or download from: https://dotnet.microsoft.com/download"
    exit 1
fi
echo "✅ .NET SDK: $(dotnet --version)"
echo ""

# Create directories
mkdir -p "$ASSEMBLIES_DIR"
mkdir -p "$SOURCE_DIR"

# Create .csproj file
echo "Creating Arsenal.csproj..."
cat > "$SOURCE_DIR/Arsenal.csproj" << 'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <OutputPath>../../Assemblies/</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <DebugType>none</DebugType>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  
  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>/Users/johnshaffer/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>/Users/johnshaffer/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed/UnityEngine.CoreModule.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="UnityEngine.IMGUIModule">
      <HintPath>/Users/johnshaffer/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed/UnityEngine.IMGUIModule.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="UnityEngine.TextRenderingModule">
      <HintPath>/Users/johnshaffer/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed/UnityEngine.TextRenderingModule.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
</Project>
CSPROJ

echo "✅ Created Arsenal.csproj"
echo ""

# Check for source files
CS_COUNT=$(find "$SOURCE_DIR" -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')
if [ "$CS_COUNT" -eq "0" ]; then
    echo "❌ ERROR: No .cs files found in $SOURCE_DIR"
    echo ""
    echo "Make sure you have these files in Source/Arsenal/:"
    echo "  - ArsenalDefOf.cs"
    echo "  - Building_Arsenal.cs"
    echo "  - Building_Hub.cs"
    echo "  - Building_Hop.cs"
    echo "  - CompMissileFuel.cs"
    echo "  - WorldObject_TravelingMissile.cs"
    echo "  - WorldObject_MissileStrike.cs"
    echo "  - ArsenalNetworkManager.cs"
    echo "  - Dialog_ConfigureArsenal.cs"
    exit 1
fi
echo "✅ Found $CS_COUNT C# source files"
echo ""

# Build
echo "=========================================="
echo "  Building..."
echo "=========================================="
cd "$SOURCE_DIR"

if dotnet build -c Release; then
    echo ""
    echo "=========================================="
    echo "  ✅ BUILD SUCCESSFUL!"
    echo "=========================================="
    
    if [ -f "$ASSEMBLIES_DIR/Arsenal.dll" ]; then
        ls -lh "$ASSEMBLIES_DIR/Arsenal.dll"
    fi
else
    echo ""
    echo "=========================================="
    echo "  ❌ BUILD FAILED"
    echo "=========================================="
    echo ""
    echo "Check the errors above."
fi