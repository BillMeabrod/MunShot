using Redux.ExtraModTypes;
namespace MunShot
{
    public class MunShotPlugin : KerbalMod
    {
        public override void OnPreInitialized() {
            /*
                Code that runs before addressables/assets are loaded goes here
                This is where you want to register loading actions or other such things 
            */
        }

        public override void OnInitialized() {
            /*
                Code that runs after addressables/assets are loaded goes here
                You are also generally free to interact with game code here
            */
            SWLogger.LogInfo("Hello World!");
        }

        public override void OnPostInitialized() {
            /*
                Code that runs after all mods have been initialized goes here
            */
        }
    }
}