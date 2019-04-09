﻿using GVFS.Common.Tracing;
using System;
using System.Threading;

namespace GVFS.Common.Git
{
    public class LibGit2RepoInvoker : IDisposable
    {
        private static readonly TimeSpan DefaultRepositoryDisposalPeriod = TimeSpan.FromMinutes(15);

        private readonly Func<LibGit2Repo> createRepo;
        private readonly ITracer tracer;
        private readonly object sharedRepoLock = new object();
        private readonly TimeSpan sharedRepositoryDisposalPeriod;
        private volatile bool disposing;
        private volatile int activeCallers;
        private LibGit2Repo sharedRepo;
        private Timer sharedRepoDisposalTimer;

        public LibGit2RepoInvoker(ITracer tracer, Func<LibGit2Repo> createRepo, TimeSpan? disposalPeriod = null)
        {
            this.tracer = tracer;
            this.createRepo = createRepo;

            if (!disposalPeriod.HasValue || disposalPeriod.Value <= TimeSpan.Zero)
            {
                this.sharedRepositoryDisposalPeriod = DefaultRepositoryDisposalPeriod;
            }
            else
            {
                this.sharedRepositoryDisposalPeriod = disposalPeriod.Value;
            }

            this.sharedRepoDisposalTimer = new Timer(
                (state) => this.DisposeSharedRepo(),
                state: null,
                dueTime: this.sharedRepositoryDisposalPeriod,
                period: this.sharedRepositoryDisposalPeriod);
        }

        public bool IsActive => this.sharedRepo != null;

        public void Dispose()
        {
            this.disposing = true;

            lock (this.sharedRepoLock)
            {
                this.sharedRepoDisposalTimer?.Dispose();
                this.sharedRepoDisposalTimer = null;

                this.sharedRepo?.Dispose();
                this.sharedRepo = null;
            }
        }

        public bool TryInvoke<TResult>(Func<LibGit2Repo, TResult> function, out TResult result)
        {
            try
            {
                Interlocked.Increment(ref this.activeCallers);
                LibGit2Repo repo = this.GetSharedRepo();

                if (repo != null)
                {
                    result = function(repo);
                    return true;
                }

                result = default(TResult);
                return false;
            }
            catch (Exception e)
            {
                this.tracer.RelatedWarning("Exception while invoking libgit2: " + e.ToString(), Keywords.Telemetry);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref this.activeCallers);
            }
        }

        private LibGit2Repo GetSharedRepo()
        {
            lock (this.sharedRepoLock)
            {
                if (this.disposing)
                {
                    return null;
                }

                this.sharedRepoDisposalTimer?.Change(this.sharedRepositoryDisposalPeriod, this.sharedRepositoryDisposalPeriod);

                if (this.sharedRepo == null)
                {
                    this.sharedRepo = this.createRepo();
                }

                return this.sharedRepo;
            }
        }

        private void DisposeSharedRepo()
        {
            lock (this.sharedRepoLock)
            {
                if (this.disposing || this.activeCallers > 0)
                {
                    return;
                }

                this.sharedRepo?.Dispose();
                this.sharedRepo = null;
            }
        }
    }
}
