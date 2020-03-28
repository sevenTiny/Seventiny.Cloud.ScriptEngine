﻿using Fasterflect;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SevenTiny.Bantina.Logging;
using SevenTiny.Bantina.Security;
using SevenTiny.Bantina.Validation;
using SevenTiny.Cloud.ScriptEngine.Configs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SevenTiny.Cloud.ScriptEngine.CSharp
{
    /// <summary>
    ///  .NET Compiler Platform ("Roslyn")
    /// </summary>
    public class CSharpDynamicScriptEngine : IDynamicScriptEngine
    {
        private int _tenantId;
        private string _scriptHash;
        private readonly string _path;
        private static readonly object _lock = new object();
        private static IDictionary<string, Type> _scriptTypeDict = new ConcurrentDictionary<string, Type>();
        private static readonly ILogger _logger = new LogManager();
        private readonly static string _currentAppName = AppSettingsConfigHelper.GetAppName();

        static CSharpDynamicScriptEngine()
        {
            //初始化引用
            CSharpReferenceManager.InitMetadataReferences();
        }

        public CSharpDynamicScriptEngine()
        {
            _path = Path.Combine(AppContext.BaseDirectory, Consts.DefaultOutPutDllPath);
        }

        public DynamicScriptExecuteResult CheckScript(DynamicScript dynamicScript)
        {
            ArgumentsCheck(dynamicScript);
            PreProcessing(dynamicScript);
            return BuildDynamicScript(dynamicScript, out string errorMsg) ? DynamicScriptExecuteResult.Success() : DynamicScriptExecuteResult.Error(errorMsg);
        }

        public DynamicScriptExecuteResult<T> Execute<T>(DynamicScript dynamicScript)
        {
            ArgumentsCheck(dynamicScript);
            PreProcessing(dynamicScript);
            return RunningDynamicScript<T>(dynamicScript);
        }

        private void ArgumentsCheck(DynamicScript dynamicScript)
        {
            dynamicScript.Script.CheckNullOrEmpty("script can not be null.");
            dynamicScript.ClassFullName.CheckNullOrEmpty("classFullName cannot be null.");
            dynamicScript.FunctionName.CheckNullOrEmpty("FunctionName can not be null.");

            if (dynamicScript.Language != DynamicScriptLanguage.Csharp)
                throw new ArgumentOutOfRangeException("dynamicScript language is not csharp, please check code or language argument.");

            if (!dynamicScript.IsTrustedScript && dynamicScript.MillisecondsTimeout <= 0)
                throw new ArgumentException("if execute untrusted code,please setting the milliseconds timeout!", "dynamicScript.MillisecondsTimeout");
        }

        private void PreProcessing(DynamicScript dynamicScript)
        {
            _tenantId = dynamicScript.TenantId;
            _scriptHash = GetScriptKeyHash(dynamicScript.Script);
        }

        private DynamicScriptExecuteResult<T> RunningDynamicScript<T>(DynamicScript dynamicScript)
        {
            //检查编译
            if (!BuildDynamicScript(dynamicScript, out string errorMessage))
            {
                _logger.LogError($"Build Script Error ! Script Info:{JsonConvert.SerializeObject(dynamicScript)}");
                return DynamicScriptExecuteResult<T>.Error(errorMessage);
            }

            try
            {
                //是否开启执行分析,统计非常耗时且会带来更多GC开销，正常运行过程请关闭！
                if (dynamicScript.IsExecutionStatistics)
                {
                    Stopwatch stopwatch = new Stopwatch();  //程序执行时间
                    var startMemory = GC.GetTotalMemory(true);  //方法调用内存占用
                    stopwatch.Start();

                    var result = CallFunction<T>(dynamicScript);

                    stopwatch.Stop();
                    result.TotalMemoryAllocated = GC.GetTotalMemory(true) - startMemory;
                    result.ProcessorTime = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                return CallFunction<T>(dynamicScript);
            }
            catch (MissingMethodException missingMethod)
            {
                _logger.LogError(missingMethod, string.Format("TenantId:{0},FunctionName:{1},Language:{2},AppName:{3},ScriptHash:{4},ParameterCount:{5},ErrorMsg: {6}", _tenantId, dynamicScript.FunctionName, "CSharp", _currentAppName, _scriptHash, dynamicScript.Parameters?.Length, missingMethod.Message));

                return DynamicScriptExecuteResult<T>.Error($"function name can not be null.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, string.Format("Script objectId:{0},tenantId:{1},appName:{2},functionName:{3},errorMsg:{4}", null, dynamicScript.TenantId, _currentAppName, dynamicScript.FunctionName, ex.Message));

                return DynamicScriptExecuteResult<T>.Error(ex.Message + ",innerEx:" + ex.InnerException?.Message);
            }
        }

        private bool BuildDynamicScript(DynamicScript dynamicScript, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                if (_scriptTypeDict.ContainsKey(_scriptHash))
                    return true;

                lock (_lock)
                {
                    if (_scriptTypeDict.ContainsKey(_scriptHash))
                        return true;

                    var asm = CreateAsmExecutor(dynamicScript.Script, out errorMessage);
                    if (asm != null)
                    {
                        var type = asm.GetType(dynamicScript.ClassFullName);
                        if (type == null)
                        {
                            errorMessage = $"type [{dynamicScript.ClassFullName}] not found in the assembly [{asm.FullName}].";
                            return false;
                        }
                        _scriptTypeDict.Add(_scriptHash, type);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.ToString();

                _logger.LogError(ex, "BuildDynamicScript Error");

                return false;
            }
        }

        private string GetScriptKeyHash(string script)
        {
            return _scriptHash = string.Format(Consts.AssemblyScriptKey, DynamicScriptLanguage.Csharp, _tenantId, _currentAppName, MD5Helper.GetMd5Hash(script));
        }

        #region Build and Create Assembly
        /// <summary>
        /// Create Assembly whick will run
        /// </summary>
        /// <param name="script"></param>
        /// <param name="errorMsg"></param>
        /// <returns></returns>
        private Assembly CreateAsmExecutor(string script, out string errorMsg)
        {
            errorMsg = null;

            var assemblyName = _scriptHash;

            var references = CSharpReferenceManager.GetMetaDataReferences()[_currentAppName];

            var compilation = CSharpCompilation.Create(assemblyName,
                new[] { GetSyntaxTree(script) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(FaaSSettingsConfigHelper.IsDebugMode() ? OptimizationLevel.Debug : OptimizationLevel.Release));

            Assembly assembly = null;
            using (var assemblyStream = new MemoryStream())
            {
                using (var pdbStream = new MemoryStream())
                {
                    var emitDynamicScriptExecuteResult = compilation.Emit(assemblyStream, pdbStream);

                    if (emitDynamicScriptExecuteResult.Success)
                    {
                        var assemblyBytes = assemblyStream.GetBuffer();
                        var pdbBytes = pdbStream.GetBuffer();
                        assembly = Assembly.Load(assemblyBytes, pdbBytes);
                        //output files
                        if (FaaSSettingsConfigHelper.IsOutPutFiles())
                            OutputDynamicScriptAllFile(script, assemblyName, assemblyBytes, pdbBytes);
                    }
                    else
                    {
                        var msgs = new StringBuilder();
                        foreach (var msg in emitDynamicScriptExecuteResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => string.Format("[{0}]:{1}({2})", d.Id, d.GetMessage(), d.Location.GetLineSpan().StartLinePosition)))
                            msgs.AppendLine(msg);

                        if (FaaSSettingsConfigHelper.IsOutPutFiles())
                            WriteDynamicScriptCs(Path.Combine(EnsureOutputPath(), assemblyName + ".cs"), script);

                        errorMsg = msgs.ToString();
                        _logger.LogError(String.Format("{0}：{1}：{2}：{3}：{4}", _tenantId, "CSharp", _currentAppName, errorMsg, _scriptHash));
                    }
                }
            }

            _logger.LogInformation($"CreateAsmExecutor -> _context:{_tenantId},{"CSharp"}, {_currentAppName},{_scriptHash} _scriptTypeDict:{_scriptTypeDict?.Count} _metadataReferences:{ CSharpReferenceManager.GetMetaDataReferences()[_currentAppName]?.Count}");

            return assembly;
        }
        private SyntaxTree GetSyntaxTree(string script)
        {
            return CSharpSyntaxTree.ParseText(script, path: _scriptHash + ".cs", encoding: Encoding.UTF8);
        }
        private void OutputDynamicScriptAllFile(string script, string assemblyName, byte[] assemblyBytes, byte[] pdbBytes)
        {
            string path = EnsureOutputPath();
            WriteDynamicScriptFile(Path.Combine(path, assemblyName + ".dll"), assemblyBytes);
            WriteDynamicScriptFile(Path.Combine(path, assemblyName + ".pdb"), pdbBytes);
            WriteDynamicScriptCs(Path.Combine(path, assemblyName + ".cs"), script);
        }
        private string EnsureOutputPath()
        {
            if (!Directory.Exists(_path))
                Directory.CreateDirectory(_path);
            return _path;
        }
        private void WriteDynamicScriptFile(string filePathName, byte[] bytes)
        {
            try
            {
                if (!File.Exists(filePathName))
                    File.WriteAllBytes(filePathName, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteDynamicScriptFile Error");
            }
        }
        private void WriteDynamicScriptCs(string filePathName, string script)
        {
            try
            {
                if (!File.Exists(filePathName))
                    File.WriteAllText(filePathName, script, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteDynamicScriptCs Error");
            }
        }
        #endregion

        #region Call script function
        private DynamicScriptExecuteResult<T> CallFunction<T>(DynamicScript dynamicScript)
        {
            if (dynamicScript.FunctionName.IsNullOrEmpty())
                return DynamicScriptExecuteResult<T>.Error($"function name can not be null.");

            if (_scriptHash.IsNullOrEmpty() || !_scriptTypeDict.ContainsKey(_scriptHash))
                return DynamicScriptExecuteResult<T>.Error($"type not found.");

            var type = _scriptTypeDict[_scriptHash];

            var methodInfo = type.Method(dynamicScript.FunctionName);

            if (methodInfo == null)
                return DynamicScriptExecuteResult<T>.Error($"function name can not be null.");

            if (dynamicScript.IsTrustedScript)
            {
                return ExecuteTrustedCode<T>(type, methodInfo, dynamicScript.Parameters);
            }
            else
            {
                if (dynamicScript.MillisecondsTimeout <= 0)
                    return DynamicScriptExecuteResult<T>.Error("if execute untrusted code,please setting the milliseconds timeout!");

                return ExecuteUntrustedCode<T>(type, methodInfo, dynamicScript.MillisecondsTimeout, dynamicScript.Parameters);
            }
        }
        private DynamicScriptExecuteResult<T> ExecuteTrustedCode<T>(Type type, MethodInfo methodInfo, params object[] parameters)
        {
            object result = null;
            var parms = methodInfo.GetParameters();
            var safeParameters = SafeTypeConvertParameters(methodInfo.Name, parms, parameters);

            if (methodInfo.IsStatic)
                result = type.TryCallMethod(methodInfo.Name, true, parms.Select(t => t.Name).ToArray(), parms.Select(t => t.ParameterType).ToArray(), safeParameters);
            else
                result = Activator.CreateInstance(type).TryCallMethod(methodInfo.Name, true, parms.Select(t => t.Name).ToArray(), parms.Select(t => t.ParameterType).ToArray(), safeParameters);

            return DynamicScriptExecuteResult<T>.Success(data: (T)result);
        }
        private DynamicScriptExecuteResult<T> ExecuteUntrustedCode<T>(Type type, MethodInfo methodInfo, int millisecondsTimeout, params object[] parameters)
        {
            string errorMessage = string.Format("[Assembly:{0},Method:{1},Timeout:{2}, execution timed out.", type.Assembly.FullName, methodInfo.Name, millisecondsTimeout);
            DynamicScriptExecuteResult<T> result = DynamicScriptExecuteResult<T>.Error(errorMessage);

            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            var t = Task.Factory.StartNew(() =>
            {
                result = ExecuteTrustedCode<T>(type, methodInfo, parameters);
            }, token);

            if (!t.Wait(millisecondsTimeout, token))
            {
                tokenSource.Cancel();

                _logger.LogError(errorMessage);

                return DynamicScriptExecuteResult<T>.Error("execution timed out!");
            }

            return result;
            //这里用不同的应用程序域重构，增强沙箱支持
            //Note:.NET Core 3.0 Preview 5 start support
            //暂时不支持沙箱环境
            //if (SettingsConfig.Instance.SandboxEnable)
            //{
            //    object obj = null;
            //    var sandBoxer = new SandBoxer();
            //    obj = sandBoxer.ExecuteUntrustedCode(type, functionName, 0, parameters);
            //    sandBoxer.UnloadSandBoxer();
            //    return (T)obj;
            //}
        }

        private object[] SafeTypeConvertParameters(string method, ParameterInfo[] parameterInfos, object[] parameters)
        {
            Ensure.ArgumentNotNullOrEmpty(parameterInfos, nameof(parameterInfos));
            Ensure.ArgumentNotNullOrEmpty(parameters, nameof(parameters));

            if (parameterInfos.Length != parameters.Length)
                throw new ArgumentException(nameof(parameters), $"The number of parameters of {method} a does not match.");

            object[] result = new object[parameters.Length];

            for (int i = 0; i < parameterInfos.Length; i++)
            {
                result[i] = Convert.ChangeType(parameters[i], parameterInfos[i].ParameterType);
            }

            return result;
        }
        #endregion
    }
}
