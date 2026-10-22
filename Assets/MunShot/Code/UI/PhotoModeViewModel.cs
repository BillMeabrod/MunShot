using Unity.Properties;
using UitkForKsp2.MVVM.Core;

namespace MunShot.UI
{
    /// <summary>The photo mode overlay's data. Bound in PhotoModeWindow.uxml; kept up to date by PhotoModeWindow.</summary>
    internal class PhotoModeViewModel : ViewModelBase
    {
        private bool _hasKerbals;
        private string _selectedName = string.Empty;
        private string _fieldOfViewText = string.Empty;
        private string _toast = string.Empty;
        private bool _toastVisible;
        private bool _isOrbiting;

        /// <summary>Whether any kerbal has been brought in, so the selected kerbal's gear shows.</summary>
        [CreateProperty]
        public bool HasKerbals
        {
            get => _hasKerbals;
            set => SetField(ref _hasKerbals, value);
        }

        /// <summary>Who the Pose tab works on, or that nobody's in yet.</summary>
        [CreateProperty]
        public string SelectedName
        {
            get => _selectedName;
            set => SetField(ref _selectedName, value);
        }

        /// <summary>Whether the camera is orbiting, so what it orbits around shows.</summary>
        [CreateProperty]
        public bool IsOrbiting
        {
            get => _isOrbiting;
            set => SetField(ref _isOrbiting, value);
        }

        /// <summary>The camera's field of view, as shown next to its slider ("60°").</summary>
        [CreateProperty]
        public string FieldOfViewText
        {
            get => _fieldOfViewText;
            set => SetField(ref _fieldOfViewText, value);
        }

        /// <summary>The last thing photo mode had to say (brought in, saved, nothing below to stand on…).</summary>
        [CreateProperty]
        public string Toast
        {
            get => _toast;
            set => SetField(ref _toast, value);
        }

        [CreateProperty]
        public bool ToastVisible
        {
            get => _toastVisible;
            set => SetField(ref _toastVisible, value);
        }
    }
}
