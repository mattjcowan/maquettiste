function pascal(text) {
  return String(text).charAt(0).toUpperCase() + String(text).slice(1);
}

maquettiste.helper("record_name", (element) => pascal(element.name) + maquettiste.params.suffix);
