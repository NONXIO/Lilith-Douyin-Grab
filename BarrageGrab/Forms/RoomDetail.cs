using System;
using System.Windows.Forms;
using BarrageGrab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BarrageGrab.Forms
{
    public partial class RoomDetail : Form
    {
        public RoomDetail(RoomInfo info)
        {
            InitializeComponent();
            //小驼峰
            var json = JsonConvert.SerializeObject(info, Formatting.Indented, new JsonSerializerSettings()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            this.textBox1.Text = json;
        }

        private void RoomDetail_Load(object sender, EventArgs e)
        {
        }

        private void button1_Click(object sender, EventArgs e)
        {
            //复制到剪切板
            Clipboard.SetText(this.textBox1.Text);
            MessageBox.Show("复制成功");
        }
    }
}