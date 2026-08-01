using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility.Baselines
{
    /// <summary>
    /// Least squares via the normal equations, solved by Gaussian elimination with
    /// partial pivoting. The HAR design matrix is four to six columns wide and its
    /// regressors are heavily collinear by construction, so a small ridge term is
    /// applied to keep the solve well conditioned.
    /// </summary>
    public static class OrdinaryLeastSquares
    {
        /// <summary>
        /// Fits y on <paramref name="design"/> with an intercept prepended.
        /// </summary>
        /// <returns>Coefficients, with the intercept at index 0.</returns>
        public static double[] Fit(IReadOnlyList<double[]> design, IReadOnlyList<double> targets, double ridge = 1e-8)
        {
            if (design == null) throw new ArgumentNullException("design");
            if (targets == null) throw new ArgumentNullException("targets");
            if (design.Count != targets.Count)
                throw new ArgumentException("design and targets must have the same number of rows.");
            if (design.Count == 0)
                throw new ArgumentException("Cannot fit a regression with no observations.");

            var predictors = design[0].Length;
            var columns = predictors + 1;

            if (design.Count < columns)
                throw new ArgumentException("Fewer observations than parameters; the system is underdetermined.");

            // Normal equations: (X'X + ridge*I) beta = X'y
            var xtx = new double[columns][];
            for (int i = 0; i < columns; i++) xtx[i] = new double[columns];
            var xty = new double[columns];

            for (int row = 0; row < design.Count; row++)
            {
                var features = design[row];
                if (features.Length != predictors)
                    throw new ArgumentException("All design rows must have the same width.");

                for (int i = 0; i < columns; i++)
                {
                    var xi = i == 0 ? 1.0 : features[i - 1];
                    xty[i] += xi * targets[row];

                    for (int j = 0; j < columns; j++)
                    {
                        var xj = j == 0 ? 1.0 : features[j - 1];
                        xtx[i][j] += xi * xj;
                    }
                }
            }

            for (int i = 0; i < columns; i++) xtx[i][i] += ridge;

            return SolveInPlace(xtx, xty);
        }

        public static double Predict(double[] coefficients, double[] features)
        {
            if (coefficients == null) throw new ArgumentNullException("coefficients");
            if (features == null) throw new ArgumentNullException("features");
            if (coefficients.Length != features.Length + 1)
                throw new ArgumentException("Coefficient vector must be one longer than the feature vector.");

            var prediction = coefficients[0];
            for (int i = 0; i < features.Length; i++)
            {
                prediction += coefficients[i + 1] * features[i];
            }
            return prediction;
        }

        private static double[] SolveInPlace(double[][] a, double[] b)
        {
            var n = b.Length;

            for (int pivot = 0; pivot < n; pivot++)
            {
                var best = pivot;
                var bestMagnitude = Math.Abs(a[pivot][pivot]);
                for (int row = pivot + 1; row < n; row++)
                {
                    var magnitude = Math.Abs(a[row][pivot]);
                    if (magnitude > bestMagnitude)
                    {
                        bestMagnitude = magnitude;
                        best = row;
                    }
                }

                if (bestMagnitude < 1e-14)
                    throw new InvalidOperationException(
                        "Normal equations are singular; the design matrix has collinear or constant columns.");

                if (best != pivot)
                {
                    var swapRow = a[pivot]; a[pivot] = a[best]; a[best] = swapRow;
                    var swapValue = b[pivot]; b[pivot] = b[best]; b[best] = swapValue;
                }

                for (int row = pivot + 1; row < n; row++)
                {
                    var factor = a[row][pivot] / a[pivot][pivot];
                    if (factor == 0.0) continue;

                    for (int col = pivot; col < n; col++)
                    {
                        a[row][col] -= factor * a[pivot][col];
                    }
                    b[row] -= factor * b[pivot];
                }
            }

            var solution = new double[n];
            for (int row = n - 1; row >= 0; row--)
            {
                var accumulated = b[row];
                for (int col = row + 1; col < n; col++)
                {
                    accumulated -= a[row][col] * solution[col];
                }
                solution[row] = accumulated / a[row][row];
            }

            return solution;
        }
    }
}
