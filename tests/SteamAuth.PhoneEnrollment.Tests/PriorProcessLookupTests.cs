using System;
using System.ComponentModel;
using System.Diagnostics;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class PriorProcessLookupTests
    {
        [Fact]
        public void FindPriorProcess_SkipsInaccessibleAndExitedProcessesAndDisposesNonMatches()
        {
            const string currentPath = @"C:\Apps\AstroSDA\Steam Desktop Authenticator.exe";

            FakeProcess current = new FakeProcess(100, currentPath);
            FakeProcess accessDenied = new FakeProcess(101, () => throw new Win32Exception(5, "Access is denied"));
            FakeProcess exited = new FakeProcess(102, () => throw new InvalidOperationException("Process has exited"));
            FakeProcess differentInstall = new FakeProcess(103, @"D:\Other\Steam Desktop Authenticator.exe");
            FakeProcess matchingMixedCase = new FakeProcess(104, @"c:\apps\astrosda\steam desktop authenticator.exe");
            FakeProcess secondMatch = new FakeProcess(105, currentPath);
            FakeProcess trailingProcess = new FakeProcess(106, () => throw new Win32Exception(5, "Should not be queried after match"));

            FakeProcess[] candidates =
            {
                current,
                accessDenied,
                exited,
                differentInstall,
                matchingMixedCase,
                secondMatch,
                trailingProcess
            };

            FakeProcess found = Program.FindPriorProcess(
                100,
                currentPath,
                candidates,
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(matchingMixedCase, found);
            Assert.False(matchingMixedCase.IsDisposed);

            Assert.True(current.IsDisposed);
            Assert.True(accessDenied.IsDisposed);
            Assert.True(exited.IsDisposed);
            Assert.True(differentInstall.IsDisposed);
            Assert.True(secondMatch.IsDisposed);
            Assert.True(trailingProcess.IsDisposed);
            Assert.False(secondMatch.PathQueried);
            Assert.False(trailingProcess.PathQueried);
        }

        [Fact]
        public void FindPriorProcess_NormalizesExtendedAndRelativePathForms()
        {
            FakeProcess extendedPathMatch = new FakeProcess(201, @"\\?\C:\Apps\AstroSDA\Steam Desktop Authenticator.exe");
            FakeProcess relativePathNonMatch = new FakeProcess(202, @"C:\Other\Steam Desktop Authenticator.exe");

            FakeProcess found = Program.FindPriorProcess(
                200,
                @"C:\Apps\Sub\..\AstroSDA\Steam Desktop Authenticator.exe",
                new[] { relativePathNonMatch, extendedPathMatch },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(extendedPathMatch, found);
            Assert.False(extendedPathMatch.IsDisposed);
            Assert.True(relativePathNonMatch.IsDisposed);
        }

        [Fact]
        public void FindPriorProcess_DisposesAllCandidatesWhenNoMatchOrCurrentPathMissing()
        {
            FakeProcess first = new FakeProcess(301, @"C:\Apps\One.exe");
            FakeProcess second = new FakeProcess(302, () => throw new Win32Exception(5, "Access denied"));
            FakeProcess third = new FakeProcess(303, @"C:\Apps\Two.exe", throwOnDispose: true);
            FakeProcess fourth = new FakeProcess(304, @"C:\Apps\Three.exe");

            FakeProcess noMatch = Program.FindPriorProcess(
                300,
                @"C:\Apps\Missing.exe",
                new[] { first, null, second, third, fourth },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Null(noMatch);
            Assert.True(first.IsDisposed);
            Assert.True(second.IsDisposed);
            Assert.True(third.DisposeAttempted);
            Assert.True(fourth.IsDisposed);

            FakeProcess fifth = new FakeProcess(305, @"C:\Apps\One.exe");
            FakeProcess missingCurrentPath = Program.FindPriorProcess(
                300,
                "   ",
                new[] { fifth },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Null(missingCurrentPath);
            Assert.True(fifth.IsDisposed);
            Assert.False(fifth.PathQueried);
        }

        [Fact]
        public void PriorProcess_DoesNotReturnCurrentProcessAndCanBeDisposed()
        {
            int currentId = Environment.ProcessId;
            using (Process prior = Program.PriorProcess())
            {
                if (prior != null)
                    Assert.NotEqual(currentId, prior.Id);
            }
        }

        private sealed class FakeProcess : IDisposable
        {
            private readonly Func<string> executablePathProvider;
            private readonly bool throwOnDispose;

            public FakeProcess(int id, string executablePath, bool throwOnDispose = false)
                : this(id, () => executablePath, throwOnDispose)
            {
            }

            public FakeProcess(int id, Func<string> executablePathProvider, bool throwOnDispose = false)
            {
                Id = id;
                this.executablePathProvider = executablePathProvider;
                this.throwOnDispose = throwOnDispose;
            }

            public int Id { get; }
            public bool PathQueried { get; private set; }
            public bool DisposeAttempted { get; private set; }
            public bool IsDisposed { get; private set; }

            public string GetExecutablePath()
            {
                PathQueried = true;
                return executablePathProvider();
            }

            public void Dispose()
            {
                DisposeAttempted = true;
                if (throwOnDispose)
                    throw new InvalidOperationException("Simulated dispose failure.");
                IsDisposed = true;
            }
        }
    }
}
