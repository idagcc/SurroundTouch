using System;
using System.Drawing;
using System.Windows.Forms;

namespace SurroundTouch
{
    internal enum TouchNoticeKind { None,Waiting,Recovered,Attention }

    // UI-thread policy with an explicit monotonic clock for deterministic tests.
    internal sealed class TouchNoticePolicy
    {
        bool waiting,waitingShown,hasNotice,hasAttention;
        long began,lastNotice,lastAttention;
        string lastReason;
        internal void Waiting(long now)
        {
            if(waiting)return;
            waiting=true;waitingShown=false;began=now;
        }
        internal TouchNoticeKind Poll(long now)
        {
            if(!waiting||waitingShown||now-began<8000||!CanShow(now))return TouchNoticeKind.None;
            waitingShown=true;Record(now);return TouchNoticeKind.Waiting;
        }
        internal TouchNoticeKind Recovered(long now)
        {
            if(!waiting)return TouchNoticeKind.None;
            bool show=waitingShown||(now-began>=3000&&CanShow(now));
            Cancel();if(!show)return TouchNoticeKind.None;
            Record(now);return TouchNoticeKind.Recovered;
        }
        internal TouchNoticeKind Attention(string reason,long now)
        {
            Cancel();
            if(hasAttention&&reason==lastReason&&now-lastAttention<60000)return TouchNoticeKind.None;
            hasAttention=true;lastReason=reason;lastAttention=now;Record(now);
            return TouchNoticeKind.Attention;
        }
        internal void Cancel() { waiting=false;waitingShown=false; }
        internal static string AttentionMessage(string reason)
        {
            if(reason.IndexOf("session",StringComparison.OrdinalIgnoreCase)>=0)
                return "Windows paused the touch session. Open SurroundTouch to review and resume correction.";
            if(reason.IndexOf("display",StringComparison.OrdinalIgnoreCase)>=0||reason.IndexOf("mapping",StringComparison.OrdinalIgnoreCase)>=0)
                return "The screen layout or mapping changed. Open SurroundTouch to review it before resuming.";
            if(reason.IndexOf("identity",StringComparison.OrdinalIgnoreCase)>=0||reason.IndexOf("ambiguous",StringComparison.OrdinalIgnoreCase)>=0||reason.IndexOf("duplicate",StringComparison.OrdinalIgnoreCase)>=0)
                return "The screen assignments need checking. Open SurroundTouch to review them.";
            return "Touch correction could not resume. Open SurroundTouch to see the next step.";
        }
        bool CanShow(long now) { return !hasNotice||now-lastNotice>=60000; }
        void Record(long now) { hasNotice=true;lastNotice=now; }
    }

    // Small, silent, non-modal notice. It never activates or takes keyboard focus.
    internal sealed class TouchNotice : Form
    {
        readonly Label heading=new Label(),message=new Label(),open=new Label(),close=new Label();
        readonly Timer dismiss=new Timer { Interval=6000 };
        readonly Action showApp;
        internal TouchNotice(Action showApp)
        {
            this.showApp=showApp;
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
            StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.Dpi;
            Font=new Font("Segoe UI",9F);BackColor=Color.FromArgb(250,252,255);
            ClientSize=new Size(352,124);Padding=new Padding(14);
            heading.SetBounds(14,12,290,23);heading.Font=new Font(Font,FontStyle.Bold);
            message.SetBounds(14,38,320,42);message.ForeColor=Color.FromArgb(70,83,99);
            open.Text="Open SurroundTouch";open.SetBounds(14,91,240,20);open.ForeColor=Color.FromArgb(30,96,220);open.Cursor=Cursors.Hand;
            close.Text="×";close.SetBounds(318,9,22,24);close.TextAlign=ContentAlignment.MiddleCenter;close.Cursor=Cursors.Hand;
            Controls.AddRange(new Control[]{heading,message,open,close});
            open.Click+=delegate { Hide();dismiss.Stop();this.showApp(); };
            close.Click+=delegate { Hide();dismiss.Stop(); };
            dismiss.Tick+=delegate { Hide();dismiss.Stop(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p; } // NOACTIVATE, TOOLWINDOW
        }
        internal void Present(TouchNoticeKind kind,string reason=null)
        {
            if(kind==TouchNoticeKind.None)return;
            heading.Text=kind==TouchNoticeKind.Recovered?"Touch is back":kind==TouchNoticeKind.Waiting?"Reconnecting touch…":"Touch needs a quick check";
            message.Text=kind==TouchNoticeKind.Recovered?"Your screen assignments and calibration are restored automatically.":
                kind==TouchNoticeKind.Waiting?"A touch controller is unavailable. We’ll reconnect it automatically. Your calibration is saved.":
                TouchNoticePolicy.AttentionMessage(reason??"");
            Rectangle area=Screen.PrimaryScreen.WorkingArea;
            Location=new Point(area.Right-Width-16,area.Bottom-Height-16);
            dismiss.Stop();Show();dismiss.Start();
            Location=new Point(area.Right-Width-16,area.Bottom-Height-16);
        }
        internal void Dismiss() { dismiss.Stop();Hide(); }
        protected override void Dispose(bool disposing)
        {
            if(disposing)dismiss.Dispose();
            base.Dispose(disposing);
        }
    }
}
