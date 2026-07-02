using System.Collections.Generic;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Supplies the beams to draw each frame. Implemented by whatever owns the beam data (an
    /// MVR/DMX rig, a procedural test, …) and injected into a <see cref="StageBeamDriver"/> via
    /// <see cref="StageBeamDriver.SetSource"/>.
    ///
    /// This interface is the one-way boundary: the StageBeam renderer depends on it, the data
    /// source implements it, and StageBeam never references the source's package.
    /// </summary>
    public interface IStageBeamSource
    {
        /// <summary>
        /// Append this frame's beams to <paramref name="beams"/>. The driver passes a cleared,
        /// reusable list, so implementations should be allocation-free (no LINQ / new lists).
        /// Called once per frame from the driver's update.
        /// </summary>
        void CollectBeams(List<StageBeamInstance> beams);
    }
}
