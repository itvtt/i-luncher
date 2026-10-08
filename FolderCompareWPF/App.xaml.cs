using System.Text;

namespace FolderCompareWPF
{
    public partial class App : System.Windows.Application
    {
        public App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}