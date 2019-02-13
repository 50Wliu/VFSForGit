#!/bin/bash

KEXTFILENAME="PrjFSKext.kext"
VFSFORDIRECTORY="/usr/local/vfsforgit"
PRJFSKEXTDIRECTORY="/Library/Extensions"
GVFSCOMMANDPATH="/usr/local/bin/gvfs"
UNINSTALLERCOMMANDPATH="/usr/local/bin/uninstall_vfsforgit.sh"
INSTALLERPACKAGEID="com.vfsforgit.pkg"
KEXTID="io.gvfs.PrjFSKext"

function UnloadKext()
{
    kextLoaded=`/usr/sbin/kextstat -b "$KEXTID" | wc -l`
    if [ $kextLoaded -eq "2" ]; then
        unloadCmd="sudo /sbin/kextunload -b $KEXTID"
        echo "$unloadCmd..."
        eval $unloadCmd || exit 1
    fi
}

function UnInstallVFSForGit()
{
    if [ -d "${VFSFORDIRECTORY}" ]; then
        rmCmd="sudo /bin/rm -Rf ${VFSFORDIRECTORY}"
        echo "$rmCmd..."
        eval $rmCmd || exit 1
    fi
    
    if [ -d "${PRJFSKEXTDIRECTORY}/$KEXTFILENAME" ]; then
        rmCmd="sudo /bin/rm -Rf ${PRJFSKEXTDIRECTORY}/$KEXTFILENAME"
        echo "$rmCmd..."
        eval $rmCmd || exit 1
    fi
    
    if [ -s "${GVFSCOMMANDPATH}" ]; then
        rmCmd="sudo /bin/rm -Rf ${GVFSCOMMANDPATH}"
        echo "$rmCmd..."
        eval $rmCmd || exit 1
    fi
}

function ForgetPackage()
{
    if [ -f "/usr/sbin/pkgutil" ]; then
        forgetCmd="sudo /usr/sbin/pkgutil --forget $INSTALLERPACKAGEID"
        echo "$forgetCmd..."
        eval $forgetCmd
    fi
}

function Run()
{
    UnloadKext
    UnInstallVFSForGit
    ForgetPackage
    echo "Successfully uninstalled VFSForGit"
}

Run
