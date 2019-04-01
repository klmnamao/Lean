﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Linq;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Alphas.Analysis;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;

namespace QuantConnect.Algorithm
{
    /// <summary>
    /// Algorithm framework base class that enforces a modular approach to algorithm development
    /// </summary>
    public partial class QCAlgorithm
    {
        private readonly ISecurityValuesProvider _securityValuesProvider;

        /// <summary>
        /// Enables additional logging of framework models including:
        /// All insights, portfolio targets, order events, and any risk management altered targets
        /// </summary>
        public bool DebugMode { get; set; }

        /// <summary>
        /// Gets or sets the universe selection model.
        /// </summary>
        public IUniverseSelectionModel UniverseSelection { get; set; }

        /// <summary>
        /// Gets or sets the alpha model
        /// </summary>
        public IAlphaModel Alpha { get; set; }

        /// <summary>
        /// Gets or sets the portfolio construction model
        /// </summary>
        public IPortfolioConstructionModel PortfolioConstruction { get; set; }

        /// <summary>
        /// Gets or sets the execution model
        /// </summary>
        public IExecutionModel Execution { get; set; }

        /// <summary>
        /// Gets or sets the risk management model
        /// </summary>
        public IRiskManagementModel RiskManagement { get; set; }

        /// <summary>
        /// Called by setup handlers after Initialize and allows the algorithm a chance to organize
        /// the data gather in the Initialize method
        /// </summary>
        public void FrameworkPostInitialize()
        {
            CheckModels();

            foreach (var universe in UniverseSelection.CreateUniverses(this))
            {
                AddUniverse(universe);
            }

            if (DebugMode)
            {
                InsightsGenerated += (algorithm, data) => Log($"{Time}: {string.Join(" | ", data.Insights.OrderBy(i => i.Symbol.ToString()))}");
            }

            // emit warning message about using the framework with cash modelling
            if (IsFrameworkAlgorithm && BrokerageModel.AccountType == AccountType.Cash)
            {
                Error("These models are currently unsuitable for Cash Modeled brokerages (e.g. GDAX) and may result in unexpected trades."
                    + " To prevent possible user error we've restricted them to Margin trading. You can select margin account types with"
                    + " SetBrokerage( ... AccountType.Margin)");
            }
        }

        /// <summary>
        /// Used to send data updates to algorithm framework models
        /// </summary>
        /// <param name="slice">The current data slice</param>
        public void OnFrameworkData(Slice slice)
        {
            if (!IsFrameworkAlgorithm)
            {
                return;
            }
            if (UtcTime >= UniverseSelection.GetNextRefreshTimeUtc())
            {
                var universes = UniverseSelection.CreateUniverses(this).ToDictionary(u => u.Configuration.Symbol);

                // remove deselected universes by symbol
                foreach (var ukvp in UniverseManager)
                {
                    var universeSymbol = ukvp.Key;
                    var qcUserDefined = UserDefinedUniverse.CreateSymbol(ukvp.Value.SecurityType, ukvp.Value.Market);
                    if (universeSymbol.Equals(qcUserDefined))
                    {
                        // prevent removal of qc algorithm created user defined universes
                        continue;
                    }

                    Universe universe;
                    if (!universes.TryGetValue(universeSymbol, out universe))
                    {
                        if (ukvp.Value.DisposeRequested)
                        {
                            UniverseManager.Remove(universeSymbol);
                        }

                        // mark this universe as disposed to remove all child subscriptions
                        ukvp.Value.Dispose();
                    }
                }

                // add newly selected universes
                foreach (var ukvp in universes)
                {
                    // note: UniverseManager.Add uses TryAdd, so don't need to worry about duplicates here
                    UniverseManager.Add(ukvp);
                }
            }

            // we only want to run universe selection if there's no data available in the slice
            if (!slice.HasData)
            {
                return;
            }

            // insight timestamping handled via InsightsGenerated event handler
            var insights = Alpha.Update(this, slice).ToArray();

            // only fire insights generated event if we actually have insights
            if (insights.Length != 0)
            {
                // debug printing of generated insights
                if (DebugMode)
                {
                    Log($"{Time}: ALPHA: {string.Join(" | ", insights.Select(i => i.ToString()).OrderBy(i => i))}");
                }

                OnInsightsGenerated(insights.Select(InitializeInsightFields));
            }

            // construct portfolio targets from insights
            var targets = PortfolioConstruction.CreateTargets(this, insights).ToArray();

            // set security targets w/ those generated via portfolio construction module
            foreach (var target in targets)
            {
                var security = Securities[target.Symbol];
                security.Holdings.Target = target;
            }

            if (DebugMode)
            {
                // debug printing of generated targets
                if (targets.Length > 0)
                {
                    Log($"{Time}: PORTFOLIO: {string.Join(" | ", targets.Select(t => t.ToString()).OrderBy(t => t))}");
                }
            }

            var riskTargetOverrides = RiskManagement.ManageRisk(this, targets).ToArray();

            // override security targets w/ those generated via risk management module
            foreach (var target in riskTargetOverrides)
            {
                var security = Securities[target.Symbol];
                security.Holdings.Target = target;
            }

            if (DebugMode)
            {
                // debug printing of generated risk target overrides
                if (riskTargetOverrides.Length > 0)
                {
                    Log($"{Time}: RISK: {string.Join(" | ", riskTargetOverrides.Select(t => t.ToString()).OrderBy(t => t))}");
                }
            }

            // execute on the targets, overriding targets for symbols w/ risk targets
            var riskAdjustedTargets = riskTargetOverrides.Concat(targets).DistinctBy(pt => pt.Symbol).ToArray();

            if (DebugMode)
            {
                // only log adjusted targets if we've performed an adjustment
                if (riskTargetOverrides.Length > 0)
                {
                    Log($"{Time}: RISK ADJUSTED TARGETS: {string.Join(" | ", riskAdjustedTargets.Select(t => t.ToString()).OrderBy(t => t))}");
                }
            }

            Execution.Execute(this, riskAdjustedTargets);
        }

        /// <summary>
        /// Used to send security changes to algorithm framework models
        /// </summary>
        /// <param name="changes">Security additions/removals for this time step</param>
        public void OnFrameworkSecuritiesChanged(SecurityChanges changes)
        {
            if (!IsFrameworkAlgorithm)
            {
                return;
            }
            if (DebugMode)
            {
                Log($"{Time}: {changes}");
            }

            Alpha.OnSecuritiesChanged(this, changes);
            PortfolioConstruction.OnSecuritiesChanged(this, changes);
            Execution.OnSecuritiesChanged(this, changes);
            RiskManagement.OnSecuritiesChanged(this, changes);
        }

        /// <summary>
        /// Sets the universe selection model
        /// </summary>
        /// <param name="universeSelection">Model defining universes for the algorithm</param>
        public void SetUniverseSelection(IUniverseSelectionModel universeSelection)
        {
            UniverseSelection = universeSelection;
        }

        /// <summary>
        /// Sets the alpha model
        /// </summary>
        /// <param name="alpha">Model that generates alpha</param>
        public void SetAlpha(IAlphaModel alpha)
        {
            Alpha = alpha;
        }

        /// <summary>
        /// Sets the portfolio construction model
        /// </summary>
        /// <param name="portfolioConstruction">Model defining how to build a portoflio from insights</param>
        public void SetPortfolioConstruction(IPortfolioConstructionModel portfolioConstruction)
        {
            PortfolioConstruction = portfolioConstruction;
        }

        /// <summary>
        /// Sets the execution model
        /// </summary>
        /// <param name="execution">Model defining how to execute trades to reach a portfolio target</param>
        public void SetExecution(IExecutionModel execution)
        {
            Execution = execution;
        }

        /// <summary>
        /// Sets the risk management model
        /// </summary>
        /// <param name="riskManagement">Model defining </param>
        public void SetRiskManagement(IRiskManagementModel riskManagement)
        {
            RiskManagement = riskManagement;
        }

        /// <summary>
        /// Manually emit insights from an algorithm.
        /// This is typically invoked before calls to submit orders in algorithms written against
        /// QCAlgorithm that have been ported into the algorithm framework.
        /// </summary>
        /// <param name="insights">The array of insights to be emitted</param>
        public void EmitInsights(params Insight[] insights)
        {
            if (!_isAlgorithmFrameworkBridge)
            {
                throw new InvalidOperationException(
                    $"This method is for backwards compatibility with {nameof(QCAlgorithmFrameworkBridge)}. " +
                    "Framework algorithms can not directly emit insights, " +
                    $"they should be generated by the {nameof(IAlphaModel)} implementation.");
            }
            OnInsightsGenerated(insights.Select(InitializeInsightFields));
        }

        /// <summary>
        /// Manually emit insights from an algorithm.
        /// This is typically invoked before calls to submit orders in algorithms written against
        /// QCAlgorithm that have been ported into the algorithm framework.
        /// </summary>
        /// <param name="insight">The insight to be emitted</param>
        public void EmitInsights(Insight insight)
        {
            if (!_isAlgorithmFrameworkBridge)
            {
                throw new InvalidOperationException(
                    $"This method is for backwards compatibility with {nameof(QCAlgorithmFrameworkBridge)}. "+
                    "Framework algorithms can not directly emit insights, " +
                    $"they should be generated by the {nameof(IAlphaModel)} implementation.");
            }
            OnInsightsGenerated(new[] { InitializeInsightFields(insight) });
        }

        /// <summary>
        /// Helper class used to set values not required to be set by alpha models
        /// </summary>
        /// <param name="insight">The <see cref="Insight"/> to set the values for</param>
        /// <returns>The same <see cref="Insight"/> instance with the values set</returns>
        private Insight InitializeInsightFields(Insight insight)
        {
            insight.GeneratedTimeUtc = UtcTime;
            insight.ReferenceValue = _securityValuesProvider.GetValues(insight.Symbol).Get(insight.Type);
            insight.SourceModel = string.IsNullOrEmpty(insight.SourceModel) ? Alpha.GetModelName() : insight.SourceModel;

            var exchangeHours = MarketHoursDatabase.GetExchangeHours(insight.Symbol.ID.Market, insight.Symbol, insight.Symbol.SecurityType);
            insight.SetPeriodAndCloseTime(exchangeHours);
            return insight;
        }

        private void CheckModels()
        {
            _isAlgorithmFrameworkBridge = GetType() == typeof(QCAlgorithmFrameworkBridge) // Py
                || GetType().IsSubclassOf(typeof(QCAlgorithmFrameworkBridge)); // C#

            if (Alpha.GetType() == typeof(NullAlphaModel)
                && !_isAlgorithmFrameworkBridge)
            {
                Log("Setting IsFrameworkAlgorithm to false");
                IsFrameworkAlgorithm = false;
            }
            // set universe model if still null, needed to wait for AddSecurity calls
            if (UniverseSelection == null)
            {
                if (!IsFrameworkAlgorithm || _isAlgorithmFrameworkBridge)
                {
                    // set universe model if still null, needed to wait for AddSecurity calls
                    SetUniverseSelection(new ManualUniverseSelectionModel());
                }
                else if (IsFrameworkAlgorithm)
                {
                    throw new Exception($"Framework algorithms must specify a portfolio selection model using the '{nameof(UniverseSelection)}' property.");
                }
            }
        }
    }
}
