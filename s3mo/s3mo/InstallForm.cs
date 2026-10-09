using s3molib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace s3mo
{
    public partial class InstallForm : Form
    {
        public InstallForm()
        {
            InitializeComponent();
        }

       private Dictionary<string, string> _filePathLookup = new Dictionary<string, string>();

        private void InstallForm_Load(object sender, EventArgs e)
        {
            string tempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");

            // Include files in subfolders (from zips); they keep their folder inside the mod.
            string[] filePaths = Directory.Exists(tempPath)
                ? Directory.GetFiles(tempPath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System })
                : Array.Empty<string>();

            if (filePaths.Length == 0)
            {
                MessageBox.Show("No file to be installed.");
                BeginInvoke(Close);
                return;
            }

            Array.Sort(filePaths, StringComparer.OrdinalIgnoreCase);
            foreach (string path in filePaths)
            {
                string relative = Path.GetRelativePath(tempPath, path);
                fileListBox.Items.Add(relative, true);
                _filePathLookup[relative] = path;
            }
        }

        private void okButton_Click(object sender, EventArgs e)
        {
            string folderName = nameTextBox.Text;
            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Mods\\{folderName}");

            if (folderName.Equals(string.Empty))
            {
                MessageBox.Show("Enter mod name.");
                return;
            }
            if (!Helper.CheckValidFolderName(folderName))
            {
                MessageBox.Show($"Invalid file name '{folderName}'.");
                return;
            }

            if (Directory.Exists(folderPath))
            {
                var r = MessageBox.Show($"'{folderName}' already existed, merge into folder and potentially replacing files?", "Mod existed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (r == DialogResult.No)
                    return;
            }
            else
            {
                try
                { Directory.CreateDirectory(folderPath); }
                catch (Exception ex)
                { MessageBox.Show(ex.Message, "Could not create folder", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }

            for (int i = 0; i < fileListBox.Items.Count; i++)
            {
                if (!fileListBox.GetItemChecked(i))
                    continue;

                string relative = fileListBox.Items[i].ToString()!;
                string filePath = _filePathLookup[relative];

                string destinationPath = Path.Combine(folderPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                File.Move(filePath, destinationPath, true);
            }

            Directory.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp"), true);
            this.Close();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            this.Close();
        }

        private void fileListBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            contextMenuStrip.Show(Cursor.Position);
        }

        private void enableAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < fileListBox.Items.Count; i++)
                fileListBox.SetItemChecked(i, true);
        }

        private void disableAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < fileListBox.Items.Count; i++)
                fileListBox.SetItemChecked(i, false);
        }

    }
}
