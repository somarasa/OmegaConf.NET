using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;

namespace OmegaConf.NET.Callbacks;

/// <summary>
/// Defines lifecycle hooks invoked during configuration composition, single-run execution, and multi-run parameter sweeps.
/// Modeled directly on Python Hydra's <c>hydra.experimental.callback.Callback</c>.
/// </summary>
public interface IConfigCallback
{
    /// <summary>
    /// Invoked immediately after a configuration tree is composed and interpolated.
    /// </summary>
    /// <param name="config">The resolved root configuration node.</param>
    /// <param name="configName">The entrypoint configuration name.</param>
    /// <param name="cliOverrides">The command-line overrides applied to the composition.</param>
    void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null);

    /// <summary>
    /// Invoked at the start of a single-run build before job execution begins.
    /// Executed in registration order.
    /// </summary>
    /// <param name="config">The resolved root configuration node.</param>
    /// <param name="configName">The entrypoint configuration name.</param>
    void OnRunStart(ConfigObjectNode config, string? configName = null);

    /// <summary>
    /// Invoked at the end of a single-run build after job execution finishes.
    /// Executed in reverse registration order.
    /// </summary>
    /// <param name="config">The resolved root configuration node.</param>
    /// <param name="configName">The entrypoint configuration name.</param>
    void OnRunEnd(ConfigObjectNode config, string? configName = null);

    /// <summary>
    /// Invoked at the start of a multi-run sweep (<c>-m</c> / <c>--multirun</c>) before individual sweep jobs are executed.
    /// Executed in registration order.
    /// </summary>
    /// <param name="config">The primary configuration node.</param>
    /// <param name="configName">The entrypoint configuration name.</param>
    void OnMultiRunStart(ConfigObjectNode config, string? configName = null);

    /// <summary>
    /// Invoked at the end of a multi-run sweep after all sweep jobs have completed.
    /// Executed in reverse registration order.
    /// </summary>
    /// <param name="jobConfigs">The resolved configuration nodes for all sweep jobs.</param>
    /// <param name="configName">The entrypoint configuration name.</param>
    void OnMultiRunEnd(IReadOnlyList<ConfigObjectNode> jobConfigs, string? configName = null);

    /// <summary>
    /// Invoked before an individual job starts (in both single-run and multi-run modes).
    /// Executed in registration order.
    /// </summary>
    /// <param name="config">The resolved configuration node for this job.</param>
    /// <param name="jobId">The zero-based job index (<c>0</c> for single-run).</param>
    void OnJobStart(ConfigObjectNode config, int jobId = 0);

    /// <summary>
    /// Invoked after an individual job completes (in both single-run and multi-run modes).
    /// Executed in reverse registration order.
    /// </summary>
    /// <param name="config">The resolved configuration node for this job.</param>
    /// <param name="jobId">The zero-based job index (<c>0</c> for single-run).</param>
    /// <param name="error">Any exception thrown during the job, or <c>null</c> if successful.</param>
    void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null);
}

/// <summary>
/// Base class with virtual no-op implementations of <see cref="IConfigCallback"/> so custom callbacks
/// only need to override the specific lifecycle hooks they use.
/// </summary>
public abstract class ConfigCallback : IConfigCallback
{
    /// <inheritdoc />
    public virtual void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null)
    {
    }

    /// <inheritdoc />
    public virtual void OnRunStart(ConfigObjectNode config, string? configName = null)
    {
    }

    /// <inheritdoc />
    public virtual void OnRunEnd(ConfigObjectNode config, string? configName = null)
    {
    }

    /// <inheritdoc />
    public virtual void OnMultiRunStart(ConfigObjectNode config, string? configName = null)
    {
    }

    /// <inheritdoc />
    public virtual void OnMultiRunEnd(IReadOnlyList<ConfigObjectNode> jobConfigs, string? configName = null)
    {
    }

    /// <inheritdoc />
    public virtual void OnJobStart(ConfigObjectNode config, int jobId = 0)
    {
    }

    /// <inheritdoc />
    public virtual void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null)
    {
    }
}

/// <summary>
/// Coordinates lifecycle callback instantiation and execution across single-run and multi-run pipelines.
/// Modeled on Python Hydra's <c>hydra._internal.callbacks.Callbacks</c>:
/// - Start hooks (<c>OnComposeConfig</c>, <c>OnRunStart</c>, <c>OnMultiRunStart</c>, <c>OnJobStart</c>) execute in registration order.
/// - End hooks (<c>OnJobEnd</c>, <c>OnRunEnd</c>, <c>OnMultiRunEnd</c>) execute in reverse registration order.
/// - Exceptions thrown by a callback are caught and recorded in <see cref="Warnings"/> so remaining callbacks still run.
/// </summary>
public sealed class CallbackManager
{
    private readonly List<IConfigCallback> _callbacks = [];
    private readonly List<string> _warnings = [];

    /// <summary>
    /// Gets the registered <see cref="IConfigCallback"/> instances in registration order.
    /// </summary>
    public IReadOnlyList<IConfigCallback> Callbacks => _callbacks;

    /// <summary>
    /// Gets the list of warning messages recorded when any callback threw an exception during dispatch.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallbackManager"/> class.
    /// </summary>
    /// <param name="programmaticCallbacks">Optional callbacks registered via <see cref="OmegaConfBuilder.AddCallback(IConfigCallback)"/>.</param>
    /// <param name="configRoot">Optional configuration tree from which <c>hydra.callbacks</c> or <c>omegaconf.callbacks</c> <c>_target_</c> entries are instantiated.</param>
    /// <param name="whitelist">Optional <see cref="ExecutionWhitelist"/> enforced when instantiating config-defined callbacks.</param>
    public CallbackManager(
        IEnumerable<IConfigCallback>? programmaticCallbacks = null,
        ConfigObjectNode? configRoot = null,
        ExecutionWhitelist? whitelist = null)
    {
        if (programmaticCallbacks != null)
        {
            _callbacks.AddRange(programmaticCallbacks.Where(c => c != null));
        }

        if (configRoot != null)
        {
            LoadCallbacksFromConfig(configRoot, whitelist);
        }
    }

    private void LoadCallbacksFromConfig(ConfigObjectNode configRoot, ExecutionWhitelist? whitelist)
    {
        foreach (var rootKey in new[] { "hydra", "omegaconf" })
        {
            if (!configRoot.TryGetChild(rootKey, out var secNode) || secNode is not ConfigObjectNode secObj)
            {
                continue;
            }

            if (!secObj.TryGetChild("callbacks", out var cbMapNode) || cbMapNode is not ConfigObjectNode cbMapObj)
            {
                continue;
            }

            foreach (var (_, cbNode) in cbMapObj.Children)
            {
                if (cbNode is ConfigObjectNode cbObj && cbObj.ContainsKey(ConfigBinder.TargetKey))
                {
                    var instance = ConfigBinder.Instantiate<IConfigCallback>(cbObj, null, whitelist);
                    if (instance != null && !_callbacks.Contains(instance))
                    {
                        _callbacks.Add(instance);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends additional <c>hydra.callbacks</c> or <c>omegaconf.callbacks</c> defined in <paramref name="configRoot"/> if not already loaded.
    /// </summary>
    public void AppendFromConfigIfAny(ConfigObjectNode configRoot, ExecutionWhitelist? whitelist = null)
    {
        if (_callbacks.Count == 0)
        {
            LoadCallbacksFromConfig(configRoot, whitelist);
        }
    }

    /// <summary>Dispatches <see cref="IConfigCallback.OnComposeConfig"/> in registration order.</summary>
    public void OnComposeConfig(ConfigObjectNode config, string? configName = null, IReadOnlyList<string>? cliOverrides = null) =>
        Notify(nameof(IConfigCallback.OnComposeConfig), reverse: false, cb => cb.OnComposeConfig(config, configName, cliOverrides));

    /// <summary>Dispatches <see cref="IConfigCallback.OnRunStart"/> in registration order.</summary>
    public void OnRunStart(ConfigObjectNode config, string? configName = null) =>
        Notify(nameof(IConfigCallback.OnRunStart), reverse: false, cb => cb.OnRunStart(config, configName));

    /// <summary>Dispatches <see cref="IConfigCallback.OnRunEnd"/> in reverse registration order.</summary>
    public void OnRunEnd(ConfigObjectNode config, string? configName = null) =>
        Notify(nameof(IConfigCallback.OnRunEnd), reverse: true, cb => cb.OnRunEnd(config, configName));

    /// <summary>Dispatches <see cref="IConfigCallback.OnMultiRunStart"/> in registration order.</summary>
    public void OnMultiRunStart(ConfigObjectNode config, string? configName = null) =>
        Notify(nameof(IConfigCallback.OnMultiRunStart), reverse: false, cb => cb.OnMultiRunStart(config, configName));

    /// <summary>Dispatches <see cref="IConfigCallback.OnMultiRunEnd"/> in reverse registration order.</summary>
    public void OnMultiRunEnd(IReadOnlyList<ConfigObjectNode> jobConfigs, string? configName = null) =>
        Notify(nameof(IConfigCallback.OnMultiRunEnd), reverse: true, cb => cb.OnMultiRunEnd(jobConfigs, configName));

    /// <summary>Dispatches <see cref="IConfigCallback.OnJobStart"/> in registration order.</summary>
    public void OnJobStart(ConfigObjectNode config, int jobId = 0) =>
        Notify(nameof(IConfigCallback.OnJobStart), reverse: false, cb => cb.OnJobStart(config, jobId));

    /// <summary>Dispatches <see cref="IConfigCallback.OnJobEnd"/> in reverse registration order.</summary>
    public void OnJobEnd(ConfigObjectNode config, int jobId = 0, Exception? error = null) =>
        Notify(nameof(IConfigCallback.OnJobEnd), reverse: true, cb => cb.OnJobEnd(config, jobId, error));

    private void Notify(string hookName, bool reverse, Action<IConfigCallback> action)
    {
        IEnumerable<IConfigCallback> sequence = reverse ? _callbacks.AsEnumerable().Reverse() : _callbacks;
        foreach (var cb in sequence)
        {
            try
            {
                action(cb);
            }
            catch (Exception ex)
            {
                _warnings.Add($"Callback '{cb.GetType().Name}.{hookName}' failed with {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
