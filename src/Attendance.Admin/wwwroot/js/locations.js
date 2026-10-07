// Office locations: fill latitude and longitude from one pasted value.
//
// Map applications copy a position as a single string, "6.465422, 3.406448".
// Typing it into two boxes by hand is where transposed or truncated
// coordinates come from, and a wrong coordinate puts an office somewhere else
// entirely. This splits a pasted pair into the two boxes.
//
// Progressive enhancement only: the paste box is hidden unless this script
// runs, and the latitude and longitude inputs are what the form submits. The
// server and the database validate the values whatever arrives.
(function () {
    'use strict';

    // Decimal degrees, separated by a comma and/or whitespace, with optional
    // degree signs: "6.465422, 3.406448", "6.465422 3.406448", "6.4654° 3.4064°".
    var pair = /^\s*(-?\d{1,3}(?:\.\d+)?)\s*°?\s*[,\s]\s*(-?\d{1,3}(?:\.\d+)?)\s*°?\s*$/;

    function round6(value) {
        return (Math.round(value * 1e6) / 1e6).toFixed(6);
    }

    function fill(box) {
        var prefix = box.getAttribute('data-coordinates-for');
        var latitude = document.getElementById(prefix + '-lat');
        var longitude = document.getElementById(prefix + '-lon');
        var hint = document.getElementById(prefix + '-paste-hint');
        var text = box.value;

        if (text.trim() === '') {
            hint.textContent = 'Paste "latitude, longitude" to fill both boxes.';
            return;
        }

        var match = pair.exec(text);
        if (!match) {
            hint.textContent = 'Not recognised. Use decimal degrees, latitude first: 6.465422, 3.406448';
            return;
        }

        var lat = parseFloat(match[1]);
        var lon = parseFloat(match[2]);

        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) {
            hint.textContent = 'Out of range: latitude must be −90 to 90, longitude −180 to 180. Is it the other way round?';
            return;
        }

        latitude.value = round6(lat);
        longitude.value = round6(lon);
        hint.textContent = 'Filled: latitude ' + latitude.value + ', longitude ' + longitude.value + '. Check them before saving.';
    }

    document.querySelectorAll('[data-coordinates-for]').forEach(function (box) {
        box.closest('.paste-coordinates').hidden = false;
        box.addEventListener('input', function () { fill(box); });
    });
}());
