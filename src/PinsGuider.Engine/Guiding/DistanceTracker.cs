// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee. All rights reserved. See THIRD_PARTY_NOTICES.md.
// Ported from PHD2 src/guider.cpp (UpdateCurrentDistance, CurrentError, CurrentErrorSmoothed) (a6c02722)

namespace PinsGuider.Engine.Guiding;

/// <summary>
/// Moving averages of the guide error distance used for settling and the jump filter
/// (PHD2 m_avgDistance / m_avgDistanceLong and their RA-only variants).
/// </summary>
public sealed class DistanceTracker
{
    /// <summary>Returned when no star was found recently (PHD2 LARGE_DISTANCE).</summary>
    public const double LargeDistance = 100.0;

    /// <summary>Seconds without a star after which the error is reported as <see cref="LargeDistance"/>.</summary>
    public const double StaleThresholdSeconds = 20;

    private const double Alpha = 0.3;
    private const double AlphaLong = 0.045;

    private double avgDistance;
    private double avgDistanceLong;
    private double avgDistanceRa;
    private double avgDistanceLongRa;
    private int avgDistanceCnt;
    private bool needReset;
    private DateTimeOffset? starFoundTimestamp;

    public void Reset()
    {
        avgDistance = avgDistanceLong = avgDistanceRa = avgDistanceLongRa = 0;
        avgDistanceCnt = 0;
        needReset = false;
        starFoundTimestamp = null;
    }

    /// <summary>Invalidate the history; the next update re-initialises the averages (PHD2 m_avgDistanceNeedReset).</summary>
    public void RequestReset() => needReset = true;

    /// <summary>Update with the latest camera-offset distance and RA-only mount distance (pixels).</summary>
    public void Update(double distance, double distanceRa, bool isGuiding, DateTimeOffset now)
    {
        starFoundTimestamp = now;

        if (isGuiding)
        {
            avgDistance += Alpha * (distance - avgDistance);
            avgDistanceRa += Alpha * (distanceRa - avgDistanceRa);

            ++avgDistanceCnt;

            if (avgDistanceCnt < 10)
            {
                // initialize smoothed running avg with mean of first 10 pts
                avgDistanceLong += (distance - avgDistanceLong) / avgDistanceCnt;
                avgDistanceLongRa += (distanceRa - avgDistanceLongRa) / avgDistanceCnt;
            }
            else
            {
                avgDistanceLong += AlphaLong * (distance - avgDistanceLong);
                avgDistanceLongRa += AlphaLong * (distanceRa - avgDistanceLongRa);
            }
        }
        else
        {
            avgDistance = avgDistanceLong = distance;
            avgDistanceRa = avgDistanceLongRa = distanceRa;
            avgDistanceCnt = 1;
        }

        if (needReset)
        {
            avgDistance = avgDistanceLong = distance;
            avgDistanceRa = avgDistanceLongRa = distanceRa;
            avgDistanceCnt = 1;
            needReset = false;
        }
    }

    /// <summary>Add a dither's displacement to the averages right away (PHD2 MoveLockPosition).</summary>
    public void AddDitherOffset(double cameraDistance, double raDistance)
    {
        avgDistance += cameraDistance;
        avgDistanceLong += cameraDistance;
        avgDistanceRa += raDistance;
        avgDistanceLongRa += raDistance;
    }

    public double CurrentError(DateTimeOffset now, bool raOnly) => Stale(now) ? LargeDistance : raOnly ? avgDistanceRa : avgDistance;

    public double CurrentErrorSmoothed(DateTimeOffset now, bool raOnly) => Stale(now) ? LargeDistance : raOnly ? avgDistanceLongRa : avgDistanceLong;

    private bool Stale(DateTimeOffset now) => starFoundTimestamp is null || (now - starFoundTimestamp.Value).TotalSeconds > StaleThresholdSeconds;
}
