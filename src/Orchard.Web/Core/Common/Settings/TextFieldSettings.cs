﻿using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Orchard.Core.Common.Settings {

    public class TextFieldSettings {
        [DataType("Flavor")]
        public string Flavor { get; set; }
        public bool Required { get; set; }
        public string Hint { get; set; }
        public string Placeholder { get; set; }
        public string DefaultValue { get; set; }
        [Range(0, int.MaxValue)]
        [DisplayName("Maximum Length")]
        public int MaxLength { get; set; }
    }
}
