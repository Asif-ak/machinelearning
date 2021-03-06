// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Command;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model.Onnx;
using Newtonsoft.Json;

[assembly: LoadableClass(SaveOnnxCommand.Summary, typeof(SaveOnnxCommand), typeof(SaveOnnxCommand.Arguments), typeof(SignatureCommand),
    "Save ONNX", "SaveOnnx", DocName = "command/SaveOnnx.md")]

namespace Microsoft.ML.Runtime.Model.Onnx
{
    public sealed class SaveOnnxCommand : DataCommand.ImplBase<SaveOnnxCommand.Arguments>
    {
        public const string Summary = "Given a data model, write out the corresponding ONNX.";
        public const string LoadName = "SaveOnnx";

        public sealed class Arguments : DataCommand.ArgumentsBase
        {
            [Argument(ArgumentType.AtMostOnce, HelpText = "The path to write the output ONNX to.", SortOrder = 1)]
            public string Onnx;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The path to write the output JSON to.", SortOrder = 2)]
            public string Json;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The 'name' property in the output ONNX. By default this will be the ONNX extension-less name.", NullName = "<Auto>", SortOrder = 3)]
            public string Name;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The 'domain' property in the output ONNX.", NullName = "<Auto>", SortOrder = 4)]
            public string Domain;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Comma delimited list of input column names to drop", ShortName = "idrop", SortOrder = 5)]
            public string InputsToDrop;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Comma delimited list of output column names to drop", ShortName = "odrop", SortOrder = 6)]
            public string OutputsToDrop;

            [Argument(ArgumentType.AtMostOnce, HelpText = "Whether we should attempt to load the predictor and attach the scorer to the pipeline if one is present.", ShortName = "pred", SortOrder = 7)]
            public bool? LoadPredictor;
        }

        private readonly string _outputModelPath;
        private readonly string _outputJsonModelPath;
        private readonly string _name;
        private readonly string _domain;
        private readonly bool? _loadPredictor;
        private readonly HashSet<string> _inputsToDrop;
        private readonly HashSet<string> _outputsToDrop;

        public SaveOnnxCommand(IHostEnvironment env, Arguments args)
                : base(env, args, LoadName)
        {
            Host.CheckValue(args, nameof(args));
            Utils.CheckOptionalUserDirectory(args.Onnx, nameof(args.Onnx));
            _outputModelPath = string.IsNullOrWhiteSpace(args.Onnx) ? null : args.Onnx;
            _outputJsonModelPath = string.IsNullOrWhiteSpace(args.Json) ? null : args.Json;
            if (args.Name == null && _outputModelPath != null)
                _name = Path.GetFileNameWithoutExtension(_outputModelPath);
            else if (!string.IsNullOrWhiteSpace(args.Name))
                _name = args.Name;

            _loadPredictor = args.LoadPredictor;
            _inputsToDrop = CreateDropMap(args.InputsToDrop);
            _outputsToDrop = CreateDropMap(args.OutputsToDrop);
            _domain = args.Domain;
        }

        private static HashSet<string> CreateDropMap(string toDrop)
        {
            if (string.IsNullOrWhiteSpace(toDrop))
                return new HashSet<string>();
            return new HashSet<string>(toDrop.Split(','));
        }

        public override void Run()
        {
            using (var ch = Host.Start("Run"))
            {
                Run(ch);
                ch.Done();
            }
        }

        private void GetPipe(IChannel ch, IDataView end, out IDataView source, out IDataView trueEnd, out LinkedList<ITransformCanSaveOnnx> transforms)
        {
            Host.AssertValue(end);
            source = trueEnd = (end as CompositeDataLoader)?.View ?? end;
            IDataTransform transform = source as IDataTransform;
            transforms = new LinkedList<ITransformCanSaveOnnx>();
            while (transform != null)
            {
                ITransformCanSaveOnnx onnxTransform = transform as ITransformCanSaveOnnx;
                if (onnxTransform == null || !onnxTransform.CanSaveOnnx)
                {
                    ch.Warning("Had to stop walkback of pipeline at {0} since it cannot save itself as ONNX.", transform.GetType().Name);
                    while (source as IDataTransform != null)
                        source = (source as IDataTransform).Source;

                    return;
                }
                transforms.AddFirst(onnxTransform);
                transform = (source = transform.Source) as IDataTransform;
            }

            Host.AssertValue(source);
        }

        private void Run(IChannel ch)
        {
            IDataLoader loader;
            IPredictor rawPred;
            RoleMappedSchema trainSchema;

            if (string.IsNullOrEmpty(Args.InputModelFile))
            {
                loader = CreateLoader();
                rawPred = null;
                trainSchema = null;
                Host.CheckUserArg(Args.LoadPredictor != true, nameof(Args.LoadPredictor),
                    "Cannot be set to true unless " + nameof(Args.InputModelFile) + " is also specifified.");
            }
            else
                LoadModelObjects(ch, _loadPredictor, out rawPred, true, out trainSchema, out loader);

            // Get the transform chain.
            IDataView source;
            IDataView end;
            LinkedList<ITransformCanSaveOnnx> transforms;
            GetPipe(ch, loader, out source, out end, out transforms);
            Host.Assert(transforms.Count == 0 || transforms.Last.Value == end);

            var ctx = new OnnxContext(Host, _name, _domain);
            // If we have a predictor, try to get the scorer for it.
            if (rawPred != null)
            {
                RoleMappedData data;
                if (trainSchema != null)
                    data = RoleMappedData.Create(end, trainSchema.GetColumnRoleNames());
                else
                {
                    // We had a predictor, but no roles stored in the model. Just suppose
                    // default column names are OK, if present.
                    data = TrainUtils.CreateExamplesOpt(end, DefaultColumnNames.Label,
                        DefaultColumnNames.Features, DefaultColumnNames.GroupId, DefaultColumnNames.Weight, DefaultColumnNames.Name);
                }

                var scorePipe = ScoreUtils.GetScorer(rawPred, data, Host, trainSchema);
                var scoreOnnx = scorePipe as ITransformCanSaveOnnx;
                if (scoreOnnx?.CanSaveOnnx == true)
                {
                    Host.Assert(scorePipe.Source == end);
                    end = scorePipe;
                    transforms.AddLast(scoreOnnx);
                }
                else
                {
                    Contracts.CheckUserArg(_loadPredictor != true,
                        nameof(Arguments.LoadPredictor), "We were explicitly told to load the predictor but we do not know how to save it as ONNX.");
                    ch.Warning("We do not know how to save the predictor as ONNX. Ignoring.");
                }
            }
            else
            {
                Contracts.CheckUserArg(_loadPredictor != true,
                    nameof(Arguments.LoadPredictor), "We were explicitly told to load the predictor but one was not present.");
            }

            HashSet<string> inputColumns = new HashSet<string>();
            //Create graph inputs.
            for (int i = 0; i < source.Schema.ColumnCount; i++)
            {
                string colName = source.Schema.GetColumnName(i);
                if(_inputsToDrop.Contains(colName))
                    continue;

                ctx.AddInputVariable(source.Schema.GetColumnType(i), colName);
                inputColumns.Add(colName);
            }

            //Create graph nodes, outputs and intermediate values.
            foreach (var trans in transforms)
            {
                Host.Assert(trans.CanSaveOnnx);
                trans.SaveAsOnnx(ctx);
            }

            //Add graph outputs.
            for (int i = 0; i < end.Schema.ColumnCount; ++i)
            {
                if (end.Schema.IsHidden(i))
                    continue;

                var idataviewColumnName = end.Schema.GetColumnName(i);;
                if (_outputsToDrop.Contains(idataviewColumnName) || _inputsToDrop.Contains(idataviewColumnName))
                    continue;

                var variableName = ctx.TryGetVariableName(idataviewColumnName);
                if (variableName != null)
                    ctx.AddOutputVariable(end.Schema.GetColumnType(i), variableName);
            }

            var model = ctx.MakeModel();
            if (_outputModelPath != null)
            {
                using (var file = Host.CreateOutputFile(_outputModelPath))
                using (var stream = file.CreateWriteStream())
                    model.WriteTo(stream);
            }

            if (_outputJsonModelPath != null)
            {
                using (var file = Host.CreateOutputFile(_outputJsonModelPath))
                using (var stream = file.CreateWriteStream())
                using (var writer = new StreamWriter(stream))
                {
                    var parsedJson = JsonConvert.DeserializeObject(model.ToString());
                    writer.Write(JsonConvert.SerializeObject(parsedJson, Formatting.Indented));
                }
            }

            if (!string.IsNullOrWhiteSpace(Args.OutputModelFile))
            {
                ch.Trace("Saving the data pipe");
                // Should probably include "end"?
                SaveLoader(loader, Args.OutputModelFile);
            }
        }
    }
}
