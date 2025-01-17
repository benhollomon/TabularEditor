﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TabularEditor.UI.Dialogs;
using TabularEditor.UIServices;

namespace TabularEditor.UI
{
    public partial class UIController
    {
        private const string NEW_DEPLOYMENT_TARGET = "<New deployment target...>";

        public void Database_Deploy()
        {
            ExpressionEditor_AcceptEdit();

            var f = new DeployForm();
            f.PreselectDb = LastDeploymentDb;
            if (LastDeployOptions != null) f.DeployOptions = LastDeployOptions;

            var res = f.ShowDialog();
            if (res == DialogResult.Cancel) return;

            LastDeployOptions = f.DeployOptions.Clone();
            LastDeploymentDb = f.PreselectDb;

            // Backup database metadata
            if (Preferences.Current.BackupOnSave)
            {
                var backupFilename = string.Format("{0}\\Backup_{1}_{2}.zip", Preferences.Current.BackupLocation, Handler.Database.Name, DateTime.Now.ToString("yyyyMMddhhmmssfff"));
                Handler.TabularDeployer.SaveModelMetadataBackup(f.DeployTargetConnectionString, f.DeployTargetDatabaseName, backupFilename);
            }


            UI.StatusLabel.Text = "Deploying...";
            Application.DoEvents();
            using (new Hourglass())
            {
                bool cancelled = false;
                bool error = false;
                string message = "";

                using (var df = new DeployingForm())
                {
                    df.DeployAction = () =>
                    {
                        try
                        {
                            Handler.Model.UpdateDeploymentMetadata(DeploymentModeMetadata.WizardUI);
                            Handler.TabularDeployer.Deploy(Handler.Database, f.DeployTargetConnectionString, f.DeployTargetDatabaseName, f.DeployOptions, df.CancelToken);
                        }
                        catch (Exception ex)
                        {
                            cancelled = df.CancelToken.IsCancellationRequested;
                            error = !cancelled;
                            message = ex.Message;
                            df.ThreadClose();
                        }
                    };
                    df.ShowDialog();
                }

                if (error || cancelled)
                {
                    MessageBox.Show(message, error ? "Error occured during deployment" : "Deploy cancelled", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                UI.StatusLabel.Text = string.Format(error ? "{0}: Deployment failed!" : cancelled ? "{0}: Deployment cancelled!" : "{0}: Deployment succeeded!", f.DeployTargetServer.Name);
            }
        }

        public LocalInstance LocalInstance { get; private set; }

        public void Database_Connect()
        {
            if (DiscardChangesCheck()) return;

            if (ConnectForm.Show() == DialogResult.Cancel) return;

            switch(ConnectForm.LocalInstance?.Type ?? LocalInstanceType.None)
            {
                case LocalInstanceType.None:
                    if (SelectDatabaseForm.Show(ConnectForm.Server) == DialogResult.Cancel) return;
                    break;
                case LocalInstanceType.Devenv:
                    MessageBox.Show("You are connecting to an integrated workspace in Visual Studio.\n\nChanges made through Tabular Editor may not be properly persisted to the Tabular Project in Visual Studio and may corrupt your model file.\n\nMake sure to keep a backup of the Model.bim file and proceed at your own risk.", "Connecting to embedded model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }

            UI.StatusLabel.Text = "Opening Model from Database...";
            ClearUI();

            Database_Open(ConnectForm.ConnectionString, ConnectForm.LocalInstance?.Type == LocalInstanceType.PowerBI ? null : SelectDatabaseForm.DatabaseName);
            UpdateUIText();
        }

        private System.Windows.Forms.Timer KeepAliveTimer;

        /// <summary>
        /// Check if the CL should be upgraded
        /// </summary>
        /// <remarks>
        /// In order to access the <see cref="TOMWrapper.Culture.Altered"/> property, a model needs a CL of at least 1571.
        /// If a Power BI "DQ over AS" data model contains any translations, and the CL is less than 1571, this method prompts
        /// the user if they want to upgrade the CL. If the user responds positively, the CL is immediately upgraded.
        /// </remarks>
        private void CL1571TranslationsCheck()
        {
            if (CL1571NoPrompt) return;

            // This flow only applies to PBI models with CL < 1571, containing at least one Entity partition (i.e. DQ over AS):
            if (Handler.Database.CompatibilityMode == Microsoft.AnalysisServices.CompatibilityMode.PowerBI && Handler.CompatibilityLevel < 1571 && Handler.Model.Tables.Any(t => t.SourceType == PartitionSourceType.Entity))
            {
                // Only prompt if the model contains translations:
                if (Handler.Model.Cultures.Any(c => c.ObjectTranslations.Count > 0))
                {
                    var mr = MessageBox.Show("This Power BI data model uses metadata translations. For composite models, it is recommended to upgrade the Compatibility Level to at least 1571 to ensure metadata translations are not overwritten from the source.\r\n\r\nDo you want to upgrade the Compatibility Level now?", "Upgrade Compatibility Level?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (mr == DialogResult.Yes)
                    {
                        Handler.Database.CompatibilityLevel = 1571;
                        foreach (var culture in Handler.Model.Cultures) culture.Altered = true;
                    }
                    CL1571NoPrompt = true;
                }
            }
            else
            {
                CL1571NoPrompt = true;
            }
        }
        private bool CL1571NoPrompt = false;

        private void PowerBIGovernanceCheck()
        {
            if (Handler.IsPbiDesktop)
            {
                if (Preferences.Current.AllowUnsupportedPBIFeatures)
                {
                    MessageBox.Show("Experimental Power BI features is enabled. You can edit any object and property of this Power BI Desktop model, but be aware that many types of changes ARE NOT CURRENTLY SUPPORTED by Microsoft.\n\nKeep a backup of your .pbix file and proceed at your own risk.", "Power BI Desktop Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else if (Handler.PowerBIGovernance.GovernanceMode == TOMWrapper.PowerBI.PowerBIGovernanceMode.ReadOnly)
                {
                    MessageBox.Show("Editing a Power BI Desktop model that does not use the Enhanced Model Metadata (V3) format is not allowed, unless you enable Experimental Power BI Features under File > Preferences.\n\nTabular Editor will still load the model in read-only mode.", "Power BI Desktop Model Read-only", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        public void Database_Open(string connectionString, string databaseName)
        {
            using (new Hourglass())
            {

                var oldHandler = Handler;

                try
                {
                    Handler = new TabularModelHandler(connectionString, databaseName);
                    PowerBIGovernanceCheck();
                    CL1571NoPrompt = false;
                    CL1571TranslationsCheck();

                    File_Current = null;
                    File_Directory = null;
                    File_SaveMode = ModelSourceType.Database;
                    if(Handler.IsPbiDesktop && ConnectForm.LocalInstance == null)
                    {
                        LocalInstance = PowerBIHelper.Instances.FirstOrDefault(i => i.Port.ToString() == Handler.Database.Server.ConnectionInfo.Port);
                    }
                    else
                        LocalInstance = ConnectForm.LocalInstance;
                    LoadTabularModelToUI();

                    Handler.OnExternalChange += Handler_OnExternalChange;
                    if (oldHandler != null) oldHandler.OnExternalChange -= Handler_OnExternalChange;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error connecting to database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Handler = oldHandler;
                    LoadTabularModelToUI();
                }

            }
        }

        private void Handler_OnExternalChange(object sender, ExternalChangeEventArgs e)
        {
            UI.FormMain.Invoke(new Action(() => {

                if (Handler.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(UI.FormMain, "A change was made to the model outside of Tabular Editor. Do you want to update the model metadata in Tabular Editor? You will lose any changes you made in Tabular Editor since the last save.", "External change detected",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (result != DialogResult.Yes) return;
                }
                Handler.RefreshTom();
                UpdateUIText();
            }));
        }

        private void Database_Save()
        {
            UI.ErrorLabel.Text = "";

            try
            {
                var conflictInfo = Handler.CheckConflicts();
                if (conflictInfo.Conflict)
                {
                    var res = MessageBox.Show(string.Format("Changes have been made to the deployed version of the model, since it was loaded into Tabular Editor.\n\nDeployed model version: {0} (changed {1})\nCurrent model version: {2}\n\nDo you want to overwrite the deployed model?",
                        conflictInfo.DatabaseVersion, conflictInfo.DatabaseLastUpdate, conflictInfo.LoadedVersion),
                        "Change conflict", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (res == DialogResult.No) return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + "\n\nSave the model as a file to make sure you do not lose any work. In case the database connection was lost, you can still apply your changes to the database by using the \"Model > Deploy\" option.", "Could not save metadata changes to database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (new Hourglass())
            {
                UI.StatusLabel.Text = "Saving changes to DB...";
                Application.DoEvents();

                if (Preferences.Current.BackupOnSave)
                {
                    var backupFilename = string.Format("{0}\\Backup_{1}_{2}.zip", Preferences.Current.BackupLocation, Handler.Database.Name, DateTime.Now.ToString("yyyyMMddhhmmssfff"));
                    try
                    {
                        Handler.TabularDeployer.SaveModelMetadataBackup(Handler.Database.Server.ConnectionString, Handler.Database.ID, backupFilename);
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.Message, "Unable to save metadata backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                try
                {
                    CL1571TranslationsCheck();
                    Handler.Model.UpdateDeploymentMetadata(DeploymentModeMetadata.SaveUI);
                    Handler.SaveDB();
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message + "\n\nSave the model as a file to make sure you do not lose any work. In case the database connection was lost, you can still apply your changes to the database by using the \"Model > Deploy\" option.", "Could not save metadata changes to database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                UI.TreeView.Refresh();

            }
        }
    }
}
