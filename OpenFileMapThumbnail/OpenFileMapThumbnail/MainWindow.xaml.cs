using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace OpenFileMapThumbnail
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FileOpenDialogo();
            dialog.Owner = this; // make it modal

            if (dialog.ShowDialog() == true)
            {
                string selectedFile = dialog.SelectedFile;
                MessageBox.Show($"You selected: {selectedFile}");
                // TODO: Load the file or process it here
            }
        }
    }
}
