using System;
using System.IO;
using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Empirical inputs of the ED process from NHAMCS ED 2016-2019 (built by Tools/make_ed_inputs.py):
    /// hourly arrival profile, acuity / ambulance / imaging / admission shares, and quantile tables (minutes) of the
    /// length of visit by acuity and disposition and of the boarding time of admitted patients.
    /// </summary>
    [Serializable]
    public class EdInputs
    {
        public string source;
        public float[] hourlyRelative = new float[24];
        public float criticalShare;
        public float ambulanceShareCritical;
        public float ambulanceShareOther;
        public float imagingShareCritical;
        public float imagingShareOther;
        public float admitShareCritical;
        public float admitShareOther;
        public float[] quantileLevels = new float[0];
        public float[] lovCriticalAdmitted = new float[0];
        public float[] lovCriticalDischarged = new float[0];
        public float[] lovOtherAdmitted = new float[0];
        public float[] lovOtherDischarged = new float[0];
        public float[] boarded = new float[0];
        public float[] waitTimeAll = new float[0];

        public static EdInputs Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("ED inputs not found: " + path);
            return JsonUtility.FromJson<EdInputs>(File.ReadAllText(path));
        }

        /// <summary>Arrival rate relative to the daily mean at a clock time (hours, may exceed 24).</summary>
        public float ArrivalRelative(float clockHours)
        {
            float h = Mathf.Repeat(clockHours, 24f);
            int i = Mathf.FloorToInt(h);
            float t = h - i;
            return Mathf.Lerp(hourlyRelative[i], hourlyRelative[(i + 1) % 24], t);
        }

        public float MaxRelative()
        {
            float m = 0f;
            foreach (float v in hourlyRelative) m = Mathf.Max(m, v);
            return m;
        }

        public float[] LovTable(bool critical, bool admitted)
        {
            if (critical) return admitted ? lovCriticalAdmitted : lovCriticalDischarged;
            return admitted ? lovOtherAdmitted : lovOtherDischarged;
        }

        /// <summary>Inverse-CDF draw from a quantile table (levels 0.01-0.99; tails are clipped to the table).</summary>
        public float Sample(float[] table, SeededRandom rng)
        {
            double u = 0.01 + 0.98 * rng.Uniform();
            return Quantile(table, (float)u);
        }

        public float Quantile(float[] table, float p)
        {
            int n = quantileLevels.Length;
            if (n == 0 || table.Length != n)
                throw new InvalidOperationException("Quantile table does not match the levels");
            if (p <= quantileLevels[0]) return table[0];
            if (p >= quantileLevels[n - 1]) return table[n - 1];
            int i = 1;
            while (quantileLevels[i] < p) i++;
            float t = (p - quantileLevels[i - 1]) / (quantileLevels[i] - quantileLevels[i - 1]);
            return Mathf.Lerp(table[i - 1], table[i], t);
        }

        /// <summary>Mean of a quantile table (trapezoid over the 0.01-0.99 levels).</summary>
        public float Mean(float[] table)
        {
            float sum = 0f;
            for (int i = 0; i < table.Length; i++) sum += table[i];
            return sum / table.Length;
        }
    }
}
