﻿using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.IO;

namespace GVFS.Common.Maintenance
{
    public abstract class GitMaintenanceStep
    {
        public const string ObjectCacheLock = "git-maintenance-step.lock";
        private readonly object gitProcessLock = new object();

        public GitMaintenanceStep(GVFSContext context, bool requireObjectCacheLock, GitProcessChecker gitProcessChecker = null)
        {
            this.Context = context;
            this.RequireObjectCacheLock = requireObjectCacheLock;
            this.GitProcessChecker = gitProcessChecker ?? new GitProcessChecker();
        }

        public abstract string Area { get; }
        protected virtual TimeSpan TimeBetweenRuns { get; }
        protected virtual string LastRunTimeFilePath { get; set; }
        protected GVFSContext Context { get; }
        protected GitProcess GitProcess { get; private set; }
        protected bool Stopping { get; private set; }
        protected bool RequireObjectCacheLock { get; }
        protected GitProcessChecker GitProcessChecker { get; }

        public void Execute()
        {
            try
            {
                if (this.RequireObjectCacheLock)
                {
                    using (FileBasedLock cacheLock = GVFSPlatform.Instance.CreateFileBasedLock(
                        this.Context.FileSystem,
                        this.Context.Tracer,
                        Path.Combine(this.Context.Enlistment.GitObjectsRoot, ObjectCacheLock)))
                    {
                        if (!cacheLock.TryAcquireLock())
                        {
                            this.Context.Tracer.RelatedInfo(this.Area + ": Skipping work since another process holds the lock");
                            return;
                        }

                        this.CreateProcessAndRun();
                    }
                }
                else
                {
                    this.CreateProcessAndRun();
                }
            }
            catch (IOException e)
            {
                this.Context.Tracer.RelatedWarning(
                    metadata: this.CreateEventMetadata(e),
                    message: "IOException while running action: " + e.Message,
                    keywords: Keywords.Telemetry);
            }
            catch (Exception e)
            {
                this.Context.Tracer.RelatedError(
                    metadata: this.CreateEventMetadata(e),
                    message: "Exception while running action: " + e.Message,
                    keywords: Keywords.Telemetry);
                Environment.Exit((int)ReturnCode.GenericError);
            }
        }

        public void Stop()
        {
            lock (this.gitProcessLock)
            {
                this.Stopping = true;

                GitProcess process = this.GitProcess;

                if (process != null)
                {
                    if (process.TryKillRunningProcess(out string processName, out int exitCode, out string error))
                    {
                        this.Context.Tracer.RelatedEvent(
                            EventLevel.Informational,
                            string.Format(
                                "{0}: killed background process {1} during {2}",
                                this.Area,
                                processName,
                                nameof(this.Stop)),
                            metadata: null);
                    }
                    else
                    {
                        this.Context.Tracer.RelatedEvent(
                            EventLevel.Informational,
                            string.Format(
                                "{0}: failed to kill background process {1} during {2}. ExitCode:{3} Error:{4}",
                                this.Area,
                                processName,
                                nameof(this.Stop),
                                exitCode,
                                error),
                            metadata: null);
                    }
                }
            }
        }

        /// <summary>
        /// Implement this method perform the mainteance actions. If the object-cache lock is required
        /// (as specified by <see cref="RequireObjectCacheLock"/>), then this step is not run unless we
        /// hold the lock.
        /// </summary>
        protected abstract void PerformMaintenance();

        protected GitProcess.Result RunGitCommand(Func<GitProcess, GitProcess.Result> work)
        {
            using (ITracer activity = this.Context.Tracer.StartActivity("RunGitCommand", EventLevel.Informational, Keywords.Telemetry, metadata: this.CreateEventMetadata()))
            {
                if (this.Stopping)
                {
                    this.Context.Tracer.RelatedWarning(
                        metadata: null,
                        message: this.Area + ": Not launching Git process because the mount is stopping",
                        keywords: Keywords.Telemetry);
                    return null;
                }

                GitProcess.Result result = work.Invoke(this.GitProcess);

                if (!this.Stopping && result?.ExitCodeIsFailure == true)
                {
                    this.Context.Tracer.RelatedWarning(
                        metadata: null,
                        message: this.Area + ": Git process failed with errors:" + result.Errors,
                        keywords: Keywords.Telemetry);
                    return result;
                }

                return result;
            }
        }

        protected EventMetadata CreateEventMetadata(Exception e = null)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", this.Area);

            if (e != null)
            {
                metadata.Add("Exception", e.ToString());
            }

            return metadata;
        }

        protected bool EnoughTimeBetweenRuns()
        {
            if (!this.Context.FileSystem.FileExists(this.LastRunTimeFilePath))
            {
                return true;
            }

            string lastRunTime = this.Context.FileSystem.ReadAllText(this.LastRunTimeFilePath);
            if (!long.TryParse(lastRunTime, out long result))
            {
                this.Context.Tracer.RelatedError("Failed to parse long: {0}", lastRunTime);
                return true;
            }

            if (DateTime.UtcNow.Subtract(EpochConverter.FromUnixEpochSeconds(result)) >= this.TimeBetweenRuns)
            {
                return true;
            }

            return false;
        }

        protected void SaveLastRunTimeToFile()
        {
            if (!this.Context.FileSystem.TryWriteTempFileAndRename(
                this.LastRunTimeFilePath,
                EpochConverter.ToUnixEpochSeconds(DateTime.UtcNow).ToString(),
                out Exception handledException))
            {
                this.Context.Tracer.RelatedError(this.CreateEventMetadata(handledException), "Failed to record run time");
            }
        }

        protected void LogErrorAndRewriteMultiPackIndex(ITracer activity, GitProcess.Result result)
        {
            EventMetadata errorMetadata = this.CreateEventMetadata();
            errorMetadata["MultiPackIndexVerifyOutput"] = result.Output;
            errorMetadata["MultiPackIndexVerifyErrors"] = result.Errors;
            string multiPackIndexPath = Path.Combine(this.Context.Enlistment.GitPackRoot, "multi-pack-index");
            errorMetadata["TryDeleteFileResult"] = this.Context.FileSystem.TryDeleteFile(multiPackIndexPath);

            GitProcess.Result rewriteResult = this.RunGitCommand((process) => process.WriteMultiPackIndex(this.Context.Enlistment.GitObjectsRoot));
            errorMetadata["RewriteResultExitCode"] = rewriteResult.ExitCode;

            activity.RelatedError(errorMetadata, "multi-pack-index is corrupt after write. Deleting and rewriting.");
        }

        protected void LogErrorAndRewriteCommitGraph(ITracer activity, GitProcess.Result result, List<string> packs)
        {
            EventMetadata errorMetadata = this.CreateEventMetadata();
            errorMetadata["CommitGraphVerifyOutput"] = result.Output;
            errorMetadata["CommitGraphVerifyErrors"] = result.Errors;
            string commitGraphPath = Path.Combine(this.Context.Enlistment.GitObjectsRoot, "info", "commit-graph");
            errorMetadata["TryDeleteFileResult"] = this.Context.FileSystem.TryDeleteFile(commitGraphPath);

            GitProcess.Result rewriteResult = this.RunGitCommand((process) => process.WriteCommitGraph(this.Context.Enlistment.GitObjectsRoot, packs));
            errorMetadata["RewriteResultExitCode"] = rewriteResult.ExitCode;

            activity.RelatedError(errorMetadata, "commit-graph is corrupt after write. Deleting and rewriting.");
        }

        private void CreateProcessAndRun()
        {
            lock (this.gitProcessLock)
            {
                if (this.Stopping)
                {
                    return;
                }

                this.GitProcess = this.Context.Enlistment.CreateGitProcess();
            }

            this.PerformMaintenance();
        }
    }
}
