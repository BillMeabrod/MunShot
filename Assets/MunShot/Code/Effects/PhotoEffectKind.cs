using System;

namespace MunShot.Effects
{
    /// <summary>The effects photo mode can place: each one of the game's own particle effects.</summary>
    public enum PhotoEffectKind
    {
        Explosion,
        SpaceExplosion,
        SolidFuelExplosion,
        Impact,
        LaunchSmoke,
        Fire,
        Steam,
        Sparks,
        GasPuff
    }

    /// <summary>The headings the Add effect list sorts effects under, in the list's order.</summary>
    public enum PhotoEffectGroup
    {
        Explosions,
        SmokeAndFire,
        Small
    }

    /// <summary>
    /// What each <see cref="PhotoEffectKind"/> is made from, where it's placed and the moment it starts at. How long it
    /// lasts is measured from the effect itself (<see cref="PhotoEffect.MaxMoment"/>).
    /// </summary>
    public readonly struct PhotoEffectSpec
    {
        /// <summary>The game's prefab, by its address.</summary>
        public string Address { get; }

        /// <summary>The name's localization key.</summary>
        public string NameKey { get; }

        /// <summary>The heading it's listed under.</summary>
        public PhotoEffectGroup Group { get; }

        /// <summary>How far in front of the camera it's placed, in metres: about its size, so it fits the view.</summary>
        public float DistanceInFront { get; }

        /// <summary>
        /// The moment it's held at when added, in seconds: where it looks fullest, picked by eye for each (sparks fly
        /// apart almost at once; an explosion's fireball is thickest a quarter of a second in; a fire or steam that
        /// keeps going is held once it's fully going).
        /// </summary>
        public float StartMoment { get; }

        private PhotoEffectSpec(string address, string nameKey, PhotoEffectGroup group, float distanceInFront, float startMoment)
        {
            Address = address;
            NameKey = nameKey;
            Group = group;
            DistanceInFront = distanceInFront;
            StartMoment = startMoment;
        }

        // Distances are about each effect's biggest particle, measured in the editor; start moments by eye
        public static PhotoEffectSpec Of(PhotoEffectKind kind) => kind switch
        {
            // A methalox tank exploding in air: fireball, flash light and sparks
            PhotoEffectKind.Explosion => Spec("fx_explosion_methalox_atmosphere", "Explosion", PhotoEffectGroup.Explosions, 25f, 0.25f),
            // The same in vacuum: no air to hold the fireball, more debris
            PhotoEffectKind.SpaceExplosion => Spec("fx_explosion_methalox_vacuum", "SpaceExplosion", PhotoEffectGroup.Explosions, 30f, 0.25f),
            // A solid booster exploding in air: the biggest fireball
            PhotoEffectKind.SolidFuelExplosion => Spec("fx_explosion_solidFuel_atmosphere", "SolidFuelExplosion", PhotoEffectGroup.Explosions, 50f, 0.25f),
            // The launch pad's billowing smoke
            PhotoEffectKind.LaunchSmoke => Spec("fx_launchsmoke_default", "LaunchSmoke", PhotoEffectGroup.SmokeAndFire, 80f, 3f),
            // The launch pad's flames, with their light; keeps burning
            PhotoEffectKind.Fire => Spec("fx_launchfire_default", "Fire", PhotoEffectGroup.SmokeAndFire, 40f, 2f),
            // Cold propellant venting; keeps going
            PhotoEffectKind.Steam => Spec("fx_cold_steam_01", "Steam", PhotoEffectGroup.SmokeAndFire, 25f, 2f),
            // A crash into the ground (loose ground; its snow and ice version looks much the same): dirt plume, flash
            // and debris
            PhotoEffectKind.Impact => Spec("fx_surfaceImpact_loose_methalox_atmosphere", "Impact", PhotoEffectGroup.Explosions, 60f, 0.5f),
            // Parts scraping
            PhotoEffectKind.Sparks => Spec("fx_spark_one_shot", "Sparks", PhotoEffectGroup.Small, 3f, 0.03f),
            // A small puff of gas, as from an RCS thruster
            PhotoEffectKind.GasPuff => Spec("fx_gasburst_03", "GasPuff", PhotoEffectGroup.Small, 4f, 0.1f),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        /// <summary>A heading's localization key.</summary>
        public static string GroupNameKey(PhotoEffectGroup group) => "MunShot/EffectGroup/" + group;

        private static PhotoEffectSpec Spec(string prefab, string name, PhotoEffectGroup group, float distanceInFront, float startMoment) =>
            new PhotoEffectSpec(prefab + ".prefab", "MunShot/Effect/" + name, group, distanceInFront, startMoment);
    }
}
