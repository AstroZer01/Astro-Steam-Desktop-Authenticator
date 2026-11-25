using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class PriorProcessLookupTests
    {
        [Fact]
        public void FindPriorProcess_SkipsInaccessibleExitedAndOtherSessionProcessesAndDisposesNonMatches()
        {
            const string currentPath = @"C:\Apps\AstroSDA\Steam Desktop Authenticator.exe";

            FakeProcess current = new FakeProcess(100, currentPath, sessionId: 1);
            FakeProcess otherSession = new FakeProcess(101, currentPath, sessionId: 2);
            FakeProcess accessDenied = new FakeProcess(102, () => throw new Win32Exception(5, "Access is denied"), sessionId: 1);
            FakeProcess exited = new FakeProcess(103, () => throw new InvalidOperationException("Process has exited"), sessionId: 1);
            FakeProcess differentInstall = new FakeProcess(104, @"D:\Other\Steam Desktop Authenticator.exe", sessionId: 1);
            FakeProcess matchingMixedCase = new FakeProcess(105, @"c:\apps\astrosda\steam desktop authenticator.exe", sessionId: 1);
            FakeProcess secondMatch = new FakeProcess(106, currentPath, sessionId: 1);
            FakeProcess trailingProcess = new FakeProcess(107, () => throw new Win32Exception(5, "Should not be queried after match"), sessionId: 1);

            FakeProcess[] candidates =
            {
                current,
                otherSession,
                accessDenied,
                exited,
                differentInstall,
                matchingMixedCase,
                secondMatch,
                trailingProcess
            };

            FakeProcess found = Program.FindPriorProcess(
                100,
                currentSessionId: 1,
                currentPath,
                candidates,
                p => p.Id,
                p => p.SessionId,
                p => p.GetExecutablePath());

            Assert.Same(matchingMixedCase, found);
            Assert.False(matchingMixedCase.IsDisposed);

            Assert.True(current.IsDisposed);
            Assert.True(otherSession.IsDisposed);
            Assert.False(otherSession.PathQueried);
            Assert.True(accessDenied.IsDisposed);
            Assert.True(exited.IsDisposed);
            Assert.True(differentInstall.IsDisposed);
            Assert.True(secondMatch.IsDisposed);
            Assert.True(trailingProcess.IsDisposed);
            Assert.False(secondMatch.PathQueried);
            Assert.False(trailingProcess.PathQueried);
        }

        [Fact]
        public void FindPriorProcess_NormalizesExtendedDeviceUncAndVolumePathFormsAndRejectsInvalidOrRelativePaths()
        {
            // Extended local, device local, and rooted dot-segment paths all normalize to the same canonical path.
            FakeProcess extendedPathMatch = new FakeProcess(201, @"\\?\C:\Apps\AstroSDA\Steam Desktop Authenticator.exe");
            FakeProcess devicePathMatch = new FakeProcess(202, @"\\.\C:\Apps\AstroSDA\Steam Desktop Authenticator.exe");
            FakeProcess otherInstallNonMatch = new FakeProcess(203, @"C:\Other\Steam Desktop Authenticator.exe");

            FakeProcess foundExtended = Program.FindPriorProcess(
                200,
                @"C:\Apps\Sub\..\AstroSDA\Steam Desktop Authenticator.exe",
                new[] { otherInstallNonMatch, extendedPathMatch },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(extendedPathMatch, foundExtended);
            Assert.False(extendedPathMatch.IsDisposed);
            Assert.True(otherInstallNonMatch.IsDisposed);

            FakeProcess foundDevice = Program.FindPriorProcess(
                200,
                @"C:\Apps\AstroSDA\Steam Desktop Authenticator.exe",
                new[] { devicePathMatch },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(devicePathMatch, foundDevice);
            Assert.False(devicePathMatch.IsDisposed);

            // Extended UNC matches .NET MainModule.FileName ("UNC\...") and "\\.\UNC\..." forms.
            FakeProcess mainModuleUncMatch = new FakeProcess(204, @"UNC\fileserver\apps\Steam Desktop Authenticator.exe");
            FakeProcess foundMainModuleUnc = Program.FindPriorProcess(
                200,
                @"\\?\UNC\fileserver\apps\Steam Desktop Authenticator.exe",
                new[] { mainModuleUncMatch },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(mainModuleUncMatch, foundMainModuleUnc);
            Assert.False(mainModuleUncMatch.IsDisposed);

            FakeProcess deviceUncMatch = new FakeProcess(205, @"\\.\UNC\fileserver\apps\Steam Desktop Authenticator.exe");
            FakeProcess foundDeviceUnc = Program.FindPriorProcess(
                200,
                @"\\fileserver\apps\Steam Desktop Authenticator.exe",
                new[] { deviceUncMatch },
                p => p.Id,
                p => p.GetExecutablePath());

            Assert.Same(deviceUncMatch, foundDeviceUnc);
            Assert.False(deviceUncMatch.IsDisposed);

            // Non-drive volume paths must stay intact instead of being stripped and resolved against CWD.
            const string volumePath = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\Apps\Steam Desktop Authenticator.exe";
            Assert.Equal(volumePath, Program.NormalizeProcessPath(volumePath));

            // Bare prefixes, whitespace-prefixed paths, NUL paths, unrooted relative paths, and drive-relative paths normalize to null.
            Assert.Null(Program.NormalizeProcessPath(@"\\?\"));
            Assert.Null(Program.NormalizeProcessPath(@"\\?\   "));
            Assert.Null(Program.NormalizeProcessPath(@"\\?\UNC\"));
            Assert.Null(Program.NormalizeProcessPath(@"\\?\UNC\   "));
            Assert.Null(Program.NormalizeProcessPath(@"\\?\UNC\\fileserver\apps\app.exe"));
            Assert.Null(Program.NormalizeProcessPath(@"\\.\"));
            Assert.Null(Program.NormalizeProcessPath(@"\\.\UNC\"));
            Assert.Null(Program.NormalizeProcessPath(@"UNC\"));
            Assert.Null(Program.NormalizeProcessPath(@"UNC\\fileserver\apps\app.exe"));
            Assert.Null(Program.NormalizeProcessPath(@"\\?\ C:\Apps\app.exe"));
            Assert.Null(Program.NormalizeProcessPath(@" C:\Apps\app.exe"));
            Assert.Null(Program.NormalizeProcessPath("C:\\Apps\\app\0.exe"));
            Assert.Null(Program.NormalizeProcessPath("Steam Desktop Authenticator.exe"));
            Assert.Null(Program.NormalizeProcessPath(@".\Steam Desktop Authenticator.exe"));
            Assert.Null(Program.NormalizeProcessPath(@"\Apps\Steam Desktop Authenticator.exe"));
            Assert.Null(Program.NormalizeProcessPath(@"C:Apps\Steam Desktop Authenticator.exe"));

            // Literal trailing spaces in extended paths must not falsely collapse onto the untrimmed path.
            string withTrailingSpace = Program.NormalizeProcessPath(@"\\?\C:\Apps\app.exe ");
            string withoutTrailingSpace = Program.NormalizeProcessPath(@"C:\Apps\app.exe");
            Assert.NotEqual(withoutTrailingSpace, withTrailingSpace, StringComparer.OrdinalIgnoreCase);

            // Bare prefix and NUL-containing paths must never match each other.
            FakeProcess barePrefixCandidate = new FakeProcess(206, @"\\?\");
            FakeProcess barePrefixResult = Program.FindPriorProcess(
                200,
                @"\\?\",
                new[] { barePrefixCandidate },
                p => p.Id,
                p => p.GetExecutablePath());
            Assert.Null(barePrefixResult);
            Assert.True(barePrefixCandidate.IsDisposed);

            FakeProcess nulCandidate = new FakeProcess(207, "C:\\Apps\\app\0.exe");
            FakeProcess nulResult = Program.FindPriorProcess(
                200,
                "C:\\Apps\\app\0.exe",
                new[] { nulCandidate },
                p => p.Id,
                p => p.GetExecutablePath());
            Assert.Null(nulResult);
            Assert.True(nulCandidate.IsDisposed);
        }

        [Fact]
        public void FindPriorProcess_PreservesMatchedInstanceOnDuplicateReferencesHandlesErrorsAndValidatesArguments()
        {
            const string currentPath = @"C:\Apps\AstroSDA\Steam Desktop Authenticator.exe";
            FakeProcess matched = new FakeProcess(401, currentPath);
            FakeProcess idThrows = new FakeProcess(402, currentPath);

            FakeProcess found = Program.FindPriorProcess(
                400,
                currentPath,
                new[] { idThrows, matched, matched },
                p => ReferenceEquals(p, idThrows) ? throw new InvalidOperationException("Process exited before reading Id") : p.Id,
                p => p.GetExecutablePath());

            Assert.Same(matched, found);
            Assert.False(matched.IsDisposed);
            Assert.True(idThrows.IsDisposed);

            Assert.Null(Program.FindPriorProcess<FakeProcess>(400, currentPath, null, p => p.Id, p => p.GetExecutablePath()));
            Assert.Null(Program.FindPriorProcess<FakeProcess>(400, currentPath, Array.Empty<FakeProcess>(), p => p.Id, p => p.GetExecutablePath()));
            Assert.Throws<ArgumentNullException>(() => Program.FindPriorProcess<FakeProcess>(400, currentPath, Array.Empty<FakeProcess>(), null, p => p.GetExecutablePath()));
            Assert.Throws<ArgumentNullException>(() => Program.FindPriorProcess<FakeProcess>(400, currentPath, Array.Empty<FakeProcess>(), p => p.Id, null));
            Assert.Throws<ArgumentNullException>(() => Program.FindPriorProcess<FakeProcess>(400, 1, currentPath, Array.Empty<FakeProcess>(), p => p.Id, null, p => p.GetExecutablePath()));

            // If indexing the candidate list throws after a match is retained, the match is disposed before rethrowing.
            FakeProcess retainedMatch = new FakeProcess(403, currentPath);
            FaultingReadOnlyList<FakeProcess> faultingList = new FaultingReadOnlyList<FakeProcess>(retainedMatch);
            Assert.Throws<InvalidOperationException>(() => Program.FindPriorProcess(
                400,
                currentPath,
                faultingList,
                p => p.Id,
                p => p.GetExecutablePath()));
            Assert.True(retainedMatch.IsDisposed);
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
        public void GetCurrentExecutablePath_ReturnsNormalizedCurrentProcessPathAndPriorProcessExcludesSelf()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                string currentPath = Program.GetCurrentExecutablePath(current);
                Assert.False(String.IsNullOrWhiteSpace(currentPath));
                Assert.NotNull(Program.NormalizeProcessPath(currentPath));
            }

            int currentId = Environment.ProcessId;
            using (Process prior = Program.PriorProcess())
            {
                if (prior != null)
                    Assert.NotEqual(currentId, prior.Id);
            }
        }

        private sealed class FaultingReadOnlyList<T> : IReadOnlyList<T>
        {
            private readonly T first;

            public FaultingReadOnlyList(T first)
            {
                this.first = first;
            }

            public T this[int index] => index == 0 ? first : throw new InvalidOperationException("Simulated list indexing failure.");
            public int Count => 2;
            public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class FakeProcess : IDisposable
        {
            private readonly Func<string> executablePathProvider;
            private readonly bool throwOnDispose;

            public FakeProcess(int id, string executablePath, bool throwOnDispose = false, int sessionId = 0)
                : this(id, () => executablePath, throwOnDispose, sessionId)
            {
            }

            public FakeProcess(int id, Func<string> executablePathProvider, bool throwOnDispose = false, int sessionId = 0)
            {
                Id = id;
                SessionId = sessionId;
                this.executablePathProvider = executablePathProvider;
                this.throwOnDispose = throwOnDispose;
            }

            public int Id { get; }
            public int SessionId { get; }
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
