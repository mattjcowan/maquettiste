function pascal(text) {
  return String(text).charAt(0).toUpperCase() + String(text).slice(1);
}

maquettiste.helper("class_name", (element) => pascal(element.name) + maquettiste.params.suffix);
