// The element's name in Pascal case, then the suffix parameter.
function pascal(text) {
  return String(text).replace(/(^|[-_ ])([a-zA-Z])/g, (_, __, c) => c.toUpperCase());
}

maquettiste.helper('class_name', (element) => pascal(element.name) + maquettiste.params.suffix);
