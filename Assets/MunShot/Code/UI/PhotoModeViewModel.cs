using System;
using System.Linq;
using MunShot.Cameras;
using MunShot.Posing;
using Unity.Properties;
using UitkForKsp2.MVVM.Commands;
using UitkForKsp2.MVVM.Core;

namespace MunShot.UI
{
    /// <summary>The photo mode overlay's side panel tabs.</summary>
    internal enum PhotoModeTab
    {
        Kerbals,
        Pose,
        Camera
    }

    /// <summary>
    /// The photo mode overlay's data and commands, as Redux's own windows have them: bound in PhotoModeWindow.uxml (the
    /// panel's buttons through UitkForKsp2's CommandBinding) and kept up to date by PhotoModeWindow.
    /// </summary>
    internal class PhotoModeViewModel : ViewModelBase
    {
        private readonly PhotoMode _photoMode;
        private bool _hasKerbals;
        private string _selectedName = string.Empty;
        private string _fieldOfViewText = string.Empty;
        private string _toast = string.Empty;
        private bool _toastVisible;
        private bool _isOrbiting;

        /// <param name="showTab">Shows a tab of the side panel (the window's own layout).</param>
        /// <param name="openKerbalMenu">Opens the list of kerbals to bring in.</param>
        public PhotoModeViewModel(PhotoMode photoMode, Action<PhotoModeTab> showTab, Action openKerbalMenu)
        {
            _photoMode = photoMode ?? throw new ArgumentNullException(nameof(photoMode));
            if (showTab == null) throw new ArgumentNullException(nameof(showTab));
            if (openKerbalMenu == null) throw new ArgumentNullException(nameof(openKerbalMenu));

            ShowKerbalsCommand = new RelayCommand(() => showTab(PhotoModeTab.Kerbals));
            ShowPoseCommand = new RelayCommand(() => showTab(PhotoModeTab.Pose));
            ShowCameraCommand = new RelayCommand(() => showTab(PhotoModeTab.Camera));
            BringInCommand = new RelayCommand(openKerbalMenu, CanBringIn);
            MoveToolCommand = new RelayCommand(() => photoMode.SetTool(HandleMode.Move));
            RotateToolCommand = new RelayCommand(() => photoMode.SetTool(HandleMode.Rotate));
            SnapToSurfaceCommand = new RelayCommand(photoMode.SnapToSurface);
            StandUprightCommand = new RelayCommand(photoMode.StandUpright);
            FreeCameraCommand = new RelayCommand(() => photoMode.SetCameraMode(PhotoCameraMode.Free));
            OrbitCameraCommand = new RelayCommand(() => photoMode.SetCameraMode(PhotoCameraMode.Orbit));
            TakePhotoCommand = new RelayCommand(photoMode.TakePhoto);
        }

        [CreateProperty] public RelayCommand ShowKerbalsCommand { get; }
        [CreateProperty] public RelayCommand ShowPoseCommand { get; }
        [CreateProperty] public RelayCommand ShowCameraCommand { get; }

        /// <summary>Opens the list of kerbals to bring in; only when someone isn't in yet.</summary>
        [CreateProperty] public RelayCommand BringInCommand { get; }

        [CreateProperty] public RelayCommand MoveToolCommand { get; }
        [CreateProperty] public RelayCommand RotateToolCommand { get; }
        [CreateProperty] public RelayCommand SnapToSurfaceCommand { get; }
        [CreateProperty] public RelayCommand StandUprightCommand { get; }
        [CreateProperty] public RelayCommand FreeCameraCommand { get; }
        [CreateProperty] public RelayCommand OrbitCameraCommand { get; }

        /// <summary>The shutter: takes the photo, as the screenshot key does.</summary>
        [CreateProperty] public RelayCommand TakePhotoCommand { get; }

        /// <summary>Lets the bound buttons check again whether they can be used (who's in has changed).</summary>
        public void RefreshCommands() => BringInCommand.RaiseCanExecuteChanged();

        private bool CanBringIn() =>
            _photoMode.NotYetIn(_photoMode.Kerbals.InScene).Any() || _photoMode.NotYetIn(_photoMode.Kerbals.Elsewhere).Any();

        /// <summary>Whether any kerbal has been brought in, so the selected kerbal's gear shows.</summary>
        [CreateProperty]
        public bool HasKerbals
        {
            get => _hasKerbals;
            set => SetField(ref _hasKerbals, value);
        }

        /// <summary>
        /// Who the Pose tab works on (their name, in capitals), or that nobody's in yet (a "#key", which the binding
        /// translates).
        /// </summary>
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
