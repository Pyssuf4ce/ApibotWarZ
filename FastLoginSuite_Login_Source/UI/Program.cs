using System;
using System.Windows.Forms;
using ApibotWarZ.UI.Forms;
using ApibotWarZ.UI.Services;

namespace ApibotWarZ.UI
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // ─── 1. Check for Auto-Update ───
            try
            {
                var updateTask = AutoUpdater.CheckForUpdateAsync();
                updateTask.Wait(TimeSpan.FromSeconds(5));

                if (updateTask.IsCompletedSuccessfully && updateTask.Result != null && updateTask.Result.HasUpdate)
                {
                    var updateForm = new UpdateForm(updateTask.Result);
                    var updateResult = updateForm.ShowDialog();

                    if (updateResult != DialogResult.Ignore && updateResult != DialogResult.Cancel)
                    {
                        return; // Updating, batch script will restart app
                    }
                }
            }
            catch { }

            // ─── 2. Cloud License Setup ───
            var licenseService = new CloudLicenseService();

            // Open License Activation Form
            var licenseForm = new LicenseForm(licenseService);
            var dialogResult = licenseForm.ShowDialog();

            if (dialogResult == DialogResult.OK && licenseForm.IsAuthenticated)
            {
                Application.Run(new MainForm(licenseForm.ExpiryText));
            }
        }
    }
}
