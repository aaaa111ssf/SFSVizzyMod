using SFS.Input;

namespace SFSVizzyMod
{
    // 弹窗期间顶掉世界输入界面：ProcessInput 为空，游戏收不到点击/拖动
    public class DialogBlocker : Screen_Base
    {
        public override bool PauseWhileOpen { get { return true; } }

        public override void ProcessInput() { }

        public override void OnOpen() { }

        public override void OnClose() { }
    }
}
