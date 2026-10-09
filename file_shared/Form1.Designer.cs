namespace file_shared
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        // Controls
        private System.Windows.Forms.Panel pnlHeader;
        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Panel pnlRules;
        private System.Windows.Forms.Label lblRules;
        private System.Windows.Forms.Panel pnlDrop;
        private System.Windows.Forms.Label lblDrop;
        private System.Windows.Forms.Button btnSelect;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.Label lblLinkTitle;
        private System.Windows.Forms.TextBox txtLink;
        private System.Windows.Forms.Button btnCopy;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Button btnImgLink;
        private System.Windows.Forms.Button btnImgHtml;
        private System.Windows.Forms.Button btnImgEmbed;
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.PictureBox picPreview;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        // 둥근 사각형 path
        private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(System.Drawing.Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 컨트롤에 둥근 모서리 적용
        private void ApplyRound(System.Windows.Forms.Control c, int radius)
        {
            c.Region = new System.Drawing.Region(RoundedPath(c.ClientRectangle, radius));
        }

        // 플랫 버튼 스타일
        private void StyleButton(System.Windows.Forms.Button b, System.Drawing.Color back,
                                 System.Drawing.Color hover, System.Drawing.Color fore, int radius)
        {
            b.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Cursor = System.Windows.Forms.Cursors.Hand;
            b.Font = new System.Drawing.Font("맑은 고딕", 9.5f, System.Drawing.FontStyle.Bold);
            ApplyRound(b, radius);
        }

        private void InitializeComponent()
        {
            this.pnlHeader = new System.Windows.Forms.Panel();
            this.lblTitle = new System.Windows.Forms.Label();
            this.btnClose = new System.Windows.Forms.Button();
            this.pnlRules = new System.Windows.Forms.Panel();
            this.lblRules = new System.Windows.Forms.Label();
            this.pnlDrop = new System.Windows.Forms.Panel();
            this.lblDrop = new System.Windows.Forms.Label();
            this.picPreview = new System.Windows.Forms.PictureBox();
            this.btnSelect = new System.Windows.Forms.Button();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.lblLinkTitle = new System.Windows.Forms.Label();
            this.txtLink = new System.Windows.Forms.TextBox();
            this.btnCopy = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnImgLink = new System.Windows.Forms.Button();
            this.btnImgHtml = new System.Windows.Forms.Button();
            this.btnImgEmbed = new System.Windows.Forms.Button();
            this.pnlHeader.SuspendLayout();
            this.pnlRules.SuspendLayout();
            this.pnlDrop.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.picPreview)).BeginInit();
            this.SuspendLayout();
            // 
            // pnlHeader
            // 
            this.pnlHeader.Controls.Add(this.lblTitle);
            this.pnlHeader.Controls.Add(this.btnClose);
            this.pnlHeader.Location = new System.Drawing.Point(0, 0);
            this.pnlHeader.Name = "pnlHeader";
            this.pnlHeader.Size = new System.Drawing.Size(560, 55);
            this.pnlHeader.TabIndex = 0;
            this.pnlHeader.Paint += new System.Windows.Forms.PaintEventHandler(this.pnlHeader_Paint);
            this.pnlHeader.MouseDown += new System.Windows.Forms.MouseEventHandler(this.pnlHeader_MouseDown);
            // 
            // lblTitle
            // 
            this.lblTitle.BackColor = System.Drawing.Color.Transparent;
            this.lblTitle.Font = new System.Drawing.Font("맑은 고딕", 16F, System.Drawing.FontStyle.Bold);
            this.lblTitle.ForeColor = System.Drawing.Color.White;
            this.lblTitle.Location = new System.Drawing.Point(24, 14);
            this.lblTitle.Name = "lblTitle";
            this.lblTitle.Size = new System.Drawing.Size(400, 32);
            this.lblTitle.TabIndex = 0;
            this.lblTitle.Text = "파일 공유";
            this.lblTitle.MouseDown += new System.Windows.Forms.MouseEventHandler(this.pnlHeader_MouseDown);
            // 
            // btnClose
            // 
            this.btnClose.BackColor = System.Drawing.Color.Transparent;
            this.btnClose.Cursor = System.Windows.Forms.Cursors.Hand;
            this.btnClose.FlatAppearance.BorderSize = 0;
            this.btnClose.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(180)))), ((int)(((byte)(10)))), ((int)(((byte)(20)))));
            this.btnClose.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(17)))), ((int)(((byte)(35)))));
            this.btnClose.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClose.Font = new System.Drawing.Font("맑은 고딕", 10F, System.Drawing.FontStyle.Bold);
            this.btnClose.ForeColor = System.Drawing.Color.White;
            this.btnClose.Location = new System.Drawing.Point(526, 6);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(28, 28);
            this.btnClose.TabIndex = 2;
            this.btnClose.Text = "✕";
            this.btnClose.UseVisualStyleBackColor = false;
            this.btnClose.Click += new System.EventHandler(this.btnClose_Click);
            // 
            // pnlRules
            // 
            this.pnlRules.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(35)))), ((int)(((byte)(35)))), ((int)(((byte)(54)))));
            this.pnlRules.Controls.Add(this.lblRules);
            this.pnlRules.Location = new System.Drawing.Point(24, 61);
            this.pnlRules.Name = "pnlRules";
            this.pnlRules.Size = new System.Drawing.Size(512, 130);
            this.pnlRules.TabIndex = 1;
            this.pnlRules.Paint += new System.Windows.Forms.PaintEventHandler(this.pnlRules_Paint);
            // 
            // lblRules
            // 
            this.lblRules.BackColor = System.Drawing.Color.Transparent;
            this.lblRules.Font = new System.Drawing.Font("맑은 고딕", 9.5F);
            this.lblRules.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(205)))), ((int)(((byte)(205)))), ((int)(((byte)(222)))));
            this.lblRules.Location = new System.Drawing.Point(20, 12);
            this.lblRules.Name = "lblRules";
            this.lblRules.Size = new System.Drawing.Size(480, 108);
            this.lblRules.TabIndex = 0;
            this.lblRules.Text = "📁 파일 공유 사용 안내\r\n• 파일을 드래그하거나 Ctrl+V로 붙여넣어 업로드\r\n• 스크린샷(캡처 이미지)도 Ctrl+V로 바로 업로드\r\n• " +
    "이미지: 링크 / HTML 미리보기 / 임베드 코드 복사\r\n• 일반 파일: 다운로드 링크 복사\r\n• 보관: 50MB 이상 7일, 미만 30일 후" +
    " 자동 삭제";
            // 
            // pnlDrop
            // 
            this.pnlDrop.AllowDrop = true;
            this.pnlDrop.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(30)))), ((int)(((byte)(48)))));
            this.pnlDrop.Controls.Add(this.lblDrop);
            this.pnlDrop.Controls.Add(this.picPreview);
            this.pnlDrop.Location = new System.Drawing.Point(24, 199);
            this.pnlDrop.Name = "pnlDrop";
            this.pnlDrop.Size = new System.Drawing.Size(512, 252);
            this.pnlDrop.TabIndex = 2;
            this.pnlDrop.DragDrop += new System.Windows.Forms.DragEventHandler(this.pnlDrop_DragDrop);
            this.pnlDrop.DragEnter += new System.Windows.Forms.DragEventHandler(this.pnlDrop_DragEnter);
            this.pnlDrop.DragLeave += new System.EventHandler(this.pnlDrop_DragLeave);
            this.pnlDrop.Paint += new System.Windows.Forms.PaintEventHandler(this.pnlDrop_Paint);
            // 
            // lblDrop
            // 
            this.lblDrop.BackColor = System.Drawing.Color.Transparent;
            this.lblDrop.Font = new System.Drawing.Font("맑은 고딕", 10.5F);
            this.lblDrop.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(170)))), ((int)(((byte)(160)))), ((int)(((byte)(220)))));
            this.lblDrop.Location = new System.Drawing.Point(0, 0);
            this.lblDrop.Name = "lblDrop";
            this.lblDrop.Size = new System.Drawing.Size(512, 52);
            this.lblDrop.TabIndex = 0;
            this.lblDrop.Text = "파일을 여기에 드래그하거나 Ctrl+V로 붙여넣기";
            this.lblDrop.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // picPreview
            // 
            this.picPreview.BackColor = System.Drawing.Color.Transparent;
            this.picPreview.Location = new System.Drawing.Point(6, 54);
            this.picPreview.Name = "picPreview";
            this.picPreview.Size = new System.Drawing.Size(500, 192);
            this.picPreview.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.picPreview.TabIndex = 1;
            this.picPreview.TabStop = false;
            this.picPreview.Visible = false;
            // 
            // btnSelect
            // 
            this.btnSelect.ForeColor = System.Drawing.Color.White;
            this.btnSelect.Location = new System.Drawing.Point(24, 469);
            this.btnSelect.Name = "btnSelect";
            this.btnSelect.Size = new System.Drawing.Size(130, 40);
            this.btnSelect.TabIndex = 3;
            this.btnSelect.Text = "파일 선택";
            this.btnSelect.Click += new System.EventHandler(this.btnSelect_Click);
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(24, 523);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(512, 14);
            this.progressBar.TabIndex = 4;
            // 
            // lblLinkTitle
            // 
            this.lblLinkTitle.Font = new System.Drawing.Font("맑은 고딕", 8.5F);
            this.lblLinkTitle.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(150)))), ((int)(((byte)(172)))));
            this.lblLinkTitle.Location = new System.Drawing.Point(24, 549);
            this.lblLinkTitle.Name = "lblLinkTitle";
            this.lblLinkTitle.Size = new System.Drawing.Size(512, 18);
            this.lblLinkTitle.TabIndex = 5;
            this.lblLinkTitle.Text = "다운로드 링크";
            // 
            // txtLink
            // 
            this.txtLink.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(30)))), ((int)(((byte)(46)))));
            this.txtLink.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.txtLink.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(220)))), ((int)(((byte)(220)))), ((int)(((byte)(235)))));
            this.txtLink.Location = new System.Drawing.Point(24, 571);
            this.txtLink.Name = "txtLink";
            this.txtLink.ReadOnly = true;
            this.txtLink.Size = new System.Drawing.Size(390, 24);
            this.txtLink.TabIndex = 6;
            // 
            // btnCopy
            // 
            this.btnCopy.ForeColor = System.Drawing.Color.White;
            this.btnCopy.Location = new System.Drawing.Point(430, 570);
            this.btnCopy.Name = "btnCopy";
            this.btnCopy.Size = new System.Drawing.Size(106, 32);
            this.btnCopy.TabIndex = 7;
            this.btnCopy.Text = "복사";
            this.btnCopy.Click += new System.EventHandler(this.btnCopy_Click);
            // 
            // lblStatus
            // 
            this.lblStatus.Font = new System.Drawing.Font("맑은 고딕", 8.5F);
            this.lblStatus.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(130)))), ((int)(((byte)(210)))), ((int)(((byte)(160)))));
            this.lblStatus.Location = new System.Drawing.Point(24, 613);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(512, 20);
            this.lblStatus.TabIndex = 8;
            // 
            // btnImgLink
            // 
            this.btnImgLink.ForeColor = System.Drawing.Color.White;
            this.btnImgLink.Location = new System.Drawing.Point(24, 645);
            this.btnImgLink.Name = "btnImgLink";
            this.btnImgLink.Size = new System.Drawing.Size(162, 36);
            this.btnImgLink.TabIndex = 9;
            this.btnImgLink.Text = "링크 복사";
            this.btnImgLink.Visible = false;
            this.btnImgLink.Click += new System.EventHandler(this.btnImgLink_Click);
            // 
            // btnImgHtml
            // 
            this.btnImgHtml.ForeColor = System.Drawing.Color.White;
            this.btnImgHtml.Location = new System.Drawing.Point(199, 645);
            this.btnImgHtml.Name = "btnImgHtml";
            this.btnImgHtml.Size = new System.Drawing.Size(162, 36);
            this.btnImgHtml.TabIndex = 10;
            this.btnImgHtml.Text = "HTML 복사";
            this.btnImgHtml.Visible = false;
            this.btnImgHtml.Click += new System.EventHandler(this.btnImgHtml_Click);
            // 
            // btnImgEmbed
            // 
            this.btnImgEmbed.ForeColor = System.Drawing.Color.White;
            this.btnImgEmbed.Location = new System.Drawing.Point(374, 645);
            this.btnImgEmbed.Name = "btnImgEmbed";
            this.btnImgEmbed.Size = new System.Drawing.Size(162, 36);
            this.btnImgEmbed.TabIndex = 11;
            this.btnImgEmbed.Text = "임베드 복사";
            this.btnImgEmbed.Visible = false;
            this.btnImgEmbed.Click += new System.EventHandler(this.btnImgEmbed_Click);
            // 
            // Form1
            // 
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(24)))), ((int)(((byte)(24)))), ((int)(((byte)(38)))));
            this.ClientSize = new System.Drawing.Size(560, 730);
            this.Controls.Add(this.pnlHeader);
            this.Controls.Add(this.pnlRules);
            this.Controls.Add(this.pnlDrop);
            this.Controls.Add(this.btnSelect);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.lblLinkTitle);
            this.Controls.Add(this.txtLink);
            this.Controls.Add(this.btnCopy);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnImgLink);
            this.Controls.Add(this.btnImgHtml);
            this.Controls.Add(this.btnImgEmbed);
            this.Font = new System.Drawing.Font("맑은 고딕", 9.5F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "파일 공유 클라이언트";
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.Form1_KeyDown);
            this.pnlHeader.ResumeLayout(false);
            this.pnlRules.ResumeLayout(false);
            this.pnlDrop.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.picPreview)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        private void pnlHeader_Paint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                this.pnlHeader.ClientRectangle,
                System.Drawing.Color.FromArgb(124, 92, 255),
                System.Drawing.Color.FromArgb(91, 63, 214),
                System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
            {
                e.Graphics.FillRectangle(b, this.pnlHeader.ClientRectangle);
            }
        }

        private void pnlRules_Paint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(124, 92, 255)))
                e.Graphics.FillRectangle(b, 0, 8, 5, this.pnlRules.Height - 16);
        }

        private void pnlDrop_Paint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rr = new System.Drawing.Rectangle(1, 1, this.pnlDrop.Width - 3, this.pnlDrop.Height - 3);
            using (var path = RoundedPath(rr, 12))
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(124, 92, 255), 1.4f))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                e.Graphics.DrawPath(pen, path);
            }
        }
    }
}