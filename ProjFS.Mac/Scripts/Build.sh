#!/bin/bash

CONFIGURATION=$1
if [ -z $CONFIGURATION ]; then
  CONFIGURATION=Debug
fi

SCRIPTDIR=$(dirname ${BASH_SOURCE[0]})
SRCDIR=$SCRIPTDIR/../..
ROOTDIR=$SRCDIR/..
PACKAGES=$ROOTDIR/packages

PROJFS=$SRCDIR/ProjFS.Mac

xcodebuild -configuration $CONFIGURATION -project $PROJFS/PrjFS.xcodeproj  -scheme 'Build All' -derivedDataPath $ROOTDIR/BuildOutput/ProjFS.Mac/Native build || exit 1

if !(gem list --local | grep xcpretty); then
  echo "Attempting to run 'sudo gem install xcpretty'.  This may ask you for your password to gain admin privileges"
  sudo gem install xcpretty
fi

# Run Tests and put output into a xml file
set -o pipefail
xcodebuild -configuration $CONFIGURATION -project $PROJFS/PrjFS.xcodeproj -scheme 'Build All' test | xcpretty -r junit --output $PROJFS/TestResultJunit.xml || exit 1
set +o pipefail

# If we're building the Profiling(Release) configuration, remove Profiling() for building .NET code
if [ "$CONFIGURATION" == "Profiling(Release)" ]; then
  CONFIGURATION=Release
fi

dotnet restore $PROJFS/PrjFSLib.Mac.Managed/PrjFSLib.Mac.Managed.csproj /p:Configuration=$CONFIGURATION /p:Platform=x64 --packages $PACKAGES || exit 1
dotnet build $PROJFS/PrjFSLib.Mac.Managed/PrjFSLib.Mac.Managed.csproj /p:Configuration=$CONFIGURATION /p:Platform=x64 || exit 1
