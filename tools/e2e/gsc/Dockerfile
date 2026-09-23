# E2E-only layer: GSC star catalog for the INDI CCD/guide simulators (they draw
# stars by running `gsc` around the snooped telescope position; without it the
# simulated frames contain only noise). Debs from the INDI PPA (mutlaqja/ppa).
ARG BASE=pins:native-guider-e2e
FROM ${BASE}
USER root
COPY *.deb /tmp/gsc/
RUN dpkg -i /tmp/gsc/gsc-data_*.deb /tmp/gsc/gsc_*.deb && rm -rf /tmp/gsc \
 && gsc -c 83.8 -5.4 -r 10 -n 5 | head -5
