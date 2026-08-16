using SteamAuth;
using System;
using SteamKit2.Authentication;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal class UserFormAuthenticator : IAuthenticator
    {
        private SteamGuardAccount account;
        private readonly Form owner;
        private readonly CancellationToken cancellationToken;
        private int deviceCodesGenerated = 0;

        public UserFormAuthenticator(SteamGuardAccount account, Form owner, CancellationToken cancellationToken = default)
        {
            this.account = account;
            this.owner = owner;
            this.cancellationToken = cancellationToken;
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            return Task.FromResult(false);
        }

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // If a code fails wait 30 seconds for a new one to regenerate
            if (previousCodeWasIncorrect)
            {
                // After 2 tries tell the user that there seems to be an issue
                if (deviceCodesGenerated > 2)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        AstroMessageBox.Show("There seems to be an issue logging into your account with these two factor codes. Are you sure SDA is still your authenticator?");
                        return true;
                    });
                }

                await Task.Delay(30000, cancellationToken);
            }

            string deviceCode;

            if (account == null)
            {
                await RunOnUiThreadAsync(() =>
                {
                    AstroMessageBox.Show("This account already has an authenticator linked. You must remove that authenticator to add SDA as your authenticator.", "Steam Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                });
                throw new OperationCanceledException(cancellationToken);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                deviceCode = await account.GenerateSteamGuardCodeAsync();
                deviceCodesGenerated++;
            }

            return deviceCode;
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<string>(cancellationToken);
            return RunOnUiThreadAsync(() =>
            {
                string message = previousCodeWasIncorrect
                    ? "That email code was not accepted. Enter the latest code Steam sent to " + email + "."
                    : "Enter the code Steam sent to " + email + ".";
                using (InputForm emailForm = new InputForm(message))
                {
                    emailForm.ShowInputDialog(owner);
                    if (emailForm.Canceled)
                        throw new OperationCanceledException(cancellationToken);
                    return emailForm.txtBox.Text;
                }
            });
        }

        private Task<T> RunOnUiThreadAsync<T>(Func<T> action)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                return Task.FromException<T>(new InvalidOperationException("The login dialog is no longer available to request a Steam confirmation code."));

            if (!owner.InvokeRequired)
            {
                try
                {
                    return Task.FromResult(action());
                }
                catch (OperationCanceledException ex)
                {
                    TaskCompletionSource<T> canceled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    canceled.TrySetCanceled(ex.CancellationToken);
                    return canceled.Task;
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellationRegistration = default;
            EventHandler ownerUnavailable = null;
            Action cleanup = null;
            int cleanupRequested = 0;
            cleanup = () =>
            {
                if (Interlocked.Exchange(ref cleanupRequested, 1) != 0)
                    return;

                owner.Disposed -= ownerUnavailable;
                owner.HandleDestroyed -= ownerUnavailable;
                cancellationRegistration.Dispose();
            };
            ownerUnavailable = (sender, args) =>
            {
                completion.TrySetCanceled();
                cleanup();
            };
            owner.Disposed += ownerUnavailable;
            owner.HandleDestroyed += ownerUnavailable;
            if (Volatile.Read(ref cleanupRequested) != 0)
            {
                owner.Disposed -= ownerUnavailable;
                owner.HandleDestroyed -= ownerUnavailable;
                return completion.Task;
            }
            cancellationRegistration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                cleanup();
            });
            if (Volatile.Read(ref cleanupRequested) != 0)
            {
                cancellationRegistration.Dispose();
                return completion.Task;
            }
            try
            {
                owner.BeginInvoke((MethodInvoker)delegate
                {
                    if (cancellationToken.IsCancellationRequested || owner.IsDisposed)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        cleanup();
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (OperationCanceledException ex)
                    {
                        completion.TrySetCanceled(ex.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                    finally
                    {
                        cleanup();
                    }
                });
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
                cleanup();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                cleanup();
            }

            return completion.Task;
        }
    }
}
