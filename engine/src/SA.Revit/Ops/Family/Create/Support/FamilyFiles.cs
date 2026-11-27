using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using SA.Revit.Host;

namespace SA.Revit.Ops.FamilyCreation.Support
{
    /// <summary>Load options that say exactly what happens to a family the project already has.</summary>
    internal sealed class ExplicitLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _replace;
        private readonly bool _familyValues;

        /// <param name="replace">Reload over a family of the same name; false leaves it untouched (the load is cancelled).</param>
        /// <param name="familyValues">On reload, existing types take the family's parameter values; false keeps the project's.</param>
        public ExplicitLoadOptions(bool replace, bool familyValues)
        {
            _replace = replace;
            _familyValues = familyValues;
        }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _familyValues;
            return _replace;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Project;
            overwriteParameterValues = false;
            return _replace;
        }
    }

    /// <summary>Saving, loading and closing family documents opened outside the job's transactions.</summary>
    internal static class FamilyFiles
    {
        /// <summary>Opens a new family document from a template. Throws with Revit's message when it cannot.</summary>
        public static Document New(Document project, string template)
        {
            Document? famDoc = project.Application.NewFamilyDocument(template);
            if (famDoc is null) throw new OpFailedException("template_failed", "Revit did not create a family from template " + template);
            return famDoc;
        }

        /// <summary>Saves the family document as <paramref name="path"/>, replacing a file there, and keeps no backups.</summary>
        public static void SaveAs(Document famDoc, string path)
        {
            var o = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 };
            famDoc.SaveAs(path, o);
        }

        /// <summary>
        /// Loads the family document into the project. Revit's failures during the load are taken through
        /// Application.FailuresProcessing: warnings come back in <paramref name="failures"/>, errors roll the load back.
        /// </summary>
        public static Family? Load(Document famDoc, Document project, IFamilyLoadOptions options, FailureCollector failures)
        {
            if (project.IsModifiable) throw new OpFailedException("transaction_open", "the project has an open transaction; Revit loads a family only outside one");
            EventHandler<FailuresProcessingEventArgs> handler = (s, e) =>
            {
                var a = e.GetFailuresAccessor();
                if (!a.GetDocument().Equals(project)) return;
                var r = failures.PreprocessFailures(a);
                if (r != FailureProcessingResult.Continue) e.SetProcessingResult(r);
            };
            project.Application.FailuresProcessing += handler;
            try { return famDoc.LoadFamily(project, options); }
            finally { project.Application.FailuresProcessing -= handler; }
        }

        public static void Close(Document? famDoc)
        {
            if (famDoc != null) OutsideDocuments.Close(famDoc);
        }

        /// <summary>Families in the project whose name equals <paramref name="name"/> ignoring case.</summary>
        public static List<Family> Named(Document project, string name) =>
            new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
